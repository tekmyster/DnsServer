using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DnsServerCore.Ipam
{
    public sealed record IpamView(string Id, string Name, string Description);
    public sealed record IpamSubnet(string Id, string ViewId, string Prefix, string Name, string Site, int? Vlan, string DhcpScope, string DnsZone, bool Container = false);
    public sealed record IpamAssignment(string Id, string SubnetId, string Address, string HostName, string Owner, string Status, string Description);
    public sealed record IpamChange(long Revision, DateTime Timestamp, string Actor, string Operation, string ObjectId, string Before, string After);
    public sealed record IpamObservation(string Address, string Source, string ObjectName, string HostName, string ClientIdentifier, DateTime? Expires);
    public sealed record IpamFinding(string Address, string Kind, string Detail);
    public sealed record IpamSnapshot(int Version, long Revision, IpamView[] Views, IpamSubnet[] Subnets, IpamAssignment[] Assignments, IpamChange[] Changes);

    // Address arithmetic never enumerates the address space, including IPv6 /64 networks.
    public sealed class IpamNetwork
    {
        public string Prefix { get; }
        public int Bits { get; }
        public int PrefixLength { get; }
        public BigInteger First { get; }
        public BigInteger Last { get; }
        public BigInteger FirstUsable => Bits == 32 && PrefixLength <= 30 || Bits == 128 && PrefixLength < 128 ? First + 1 : First;
        public BigInteger LastUsable => Bits == 32 && PrefixLength <= 30 ? Last - 1 : Last;
        public BigInteger Capacity => LastUsable - FirstUsable + 1;

        public IpamNetwork(string prefix)
        {
            string[] parts = prefix.Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out IPAddress ip) || ip.ScopeIdSafe() != 0 || ip.IsIPv4MappedToIPv6 || !int.TryParse(parts[1], out int length))
                throw new ArgumentException("A valid IPv4 or IPv6 CIDR prefix is required.");
            Bits = ip.GetAddressBytes().Length * 8;
            if (length < 0 || length > Bits)
                throw new ArgumentException("Invalid prefix length.");
            PrefixLength = length;
            BigInteger size = BigInteger.One << (Bits - length);
            First = Number(ip) / size * size;
            Last = First + size - 1;
            Prefix = Format(First) + "/" + length;
        }

        public static BigInteger Number(IPAddress ip) => new BigInteger(ip.GetAddressBytes(), isUnsigned: true, isBigEndian: true);
        public string Format(BigInteger number)
        {
            byte[] value = number.ToByteArray(isUnsigned: true, isBigEndian: true);
            byte[] bytes = new byte[Bits / 8];
            if (value.Length > bytes.Length || number < 0) throw new ArgumentOutOfRangeException(nameof(number));
            value.CopyTo(bytes, bytes.Length - value.Length);
            return new IPAddress(bytes).ToString();
        }
        public bool Contains(IPAddress ip) => ip.GetAddressBytes().Length * 8 == Bits && Number(ip) >= First && Number(ip) <= Last;
        public bool Overlaps(IpamNetwork other) => Bits == other.Bits && First <= other.Last && other.First <= Last;
    }

    static class IpamAddressExtensions
    {
        public static long ScopeIdSafe(this IPAddress address) => address.GetAddressBytes().Length == 16 ? address.ScopeId : 0;
    }

    public sealed class IpamManager
    {
        readonly object _sync = new object();
        readonly string _path;
        static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        IpamSnapshot _state;

        public IpamManager(string path)
        {
            _path = path;
            _state = File.Exists(path) ? Parse(File.ReadAllBytes(path)) : new IpamSnapshot(1, 0, [new IpamView("default", "Default", "")], [], [], []);
        }

        public IpamSnapshot Snapshot()
        {
            lock (_sync) return _state with { Views = _state.Views.ToArray(), Subnets = _state.Subnets.ToArray(), Assignments = _state.Assignments.ToArray(), Changes = _state.Changes.ToArray() };
        }

        public byte[] Export() { lock (_sync) return JsonSerializer.SerializeToUtf8Bytes(_state, JsonOptions); }
        public IpamSubnet[] GetSubnets() { lock (_sync) return _state.Subnets.ToArray(); }
        public void Restore(byte[] data)
        {
            IpamSnapshot state = Parse(data);
            lock (_sync) { Save(state); _state = state; }
        }

        static IpamSnapshot Parse(byte[] data)
        {
            IpamSnapshot state = JsonSerializer.Deserialize<IpamSnapshot>(data, JsonOptions) ?? throw new InvalidDataException("Empty IPAM inventory.");
            Validate(state);
            return state;
        }

        static void Validate(IpamSnapshot state)
        {
            if (state.Version != 1 || state.Revision < 0 || state.Views is null || state.Subnets is null || state.Assignments is null || state.Changes is null)
                throw new InvalidDataException("Unsupported or invalid IPAM inventory.");
            if (state.Views.Any(v => v is null || string.IsNullOrWhiteSpace(v.Id) || string.IsNullOrWhiteSpace(v.Name)) || state.Views.Select(v => v.Id).Distinct().Count() != state.Views.Length || state.Views.Select(v => v.Name.ToLowerInvariant()).Distinct().Count() != state.Views.Length)
                throw new ArgumentException("Network view IDs and names must be unique.");
            if (!state.Views.Any(v => v.Id == "default")) throw new ArgumentException("The default network view is required.");
            if (state.Subnets.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Name) || !state.Views.Any(v => v.Id == s.ViewId) || s.Vlan < 1 || s.Vlan > 4094) || state.Subnets.Select(s => s.Id).Distinct().Count() != state.Subnets.Length)
                throw new ArgumentException("Invalid subnet or network view.");
            for (int i = 0; i < state.Subnets.Length; i++)
            {
                IpamSubnet subnet = state.Subnets[i];
                IpamNetwork network = new IpamNetwork(subnet.Prefix);
                if (network.Prefix != subnet.Prefix) throw new ArgumentException("Subnet prefixes must be canonical.");
                if (subnet.Container && (!string.IsNullOrEmpty(subnet.DhcpScope) || !string.IsNullOrEmpty(subnet.DnsZone))) throw new ArgumentException("Containers cannot link to services.");
                for (int j = 0; j < i; j++)
                {
                    IpamSubnet other = state.Subnets[j];
                    IpamNetwork otherNetwork = new IpamNetwork(other.Prefix);
                    if (subnet.ViewId == other.ViewId && network.Overlaps(otherNetwork) && !(subnet.Container && network.PrefixLength < otherNetwork.PrefixLength && network.First <= otherNetwork.First && network.Last >= otherNetwork.Last) && !(other.Container && otherNetwork.PrefixLength < network.PrefixLength && otherNetwork.First <= network.First && otherNetwork.Last >= network.Last)) throw new ArgumentException("Only container prefixes may contain other prefixes in one network view.");
                    if (!string.IsNullOrEmpty(subnet.DhcpScope) && string.Equals(subnet.DhcpScope, other.DhcpScope, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A DHCP scope can be linked to only one subnet.");
                    if (!string.IsNullOrEmpty(subnet.DnsZone) && string.Equals(subnet.DnsZone, other.DnsZone, StringComparison.OrdinalIgnoreCase) && subnet.ViewId != other.ViewId) throw new ArgumentException("A DNS zone cannot identify overlapping network views.");
                }
            }
            HashSet<string> assigned = [];
            HashSet<string> ids = [];
            foreach (IpamAssignment assignment in state.Assignments)
            {
                if (assignment is null || string.IsNullOrEmpty(assignment.Id) || !ids.Add(assignment.Id)) throw new ArgumentException("Invalid assignment ID.");
                IpamSubnet subnet = state.Subnets.SingleOrDefault(s => s.Id == assignment.SubnetId) ?? throw new ArgumentException("Unknown assignment subnet.");
                IpamNetwork network = new IpamNetwork(subnet.Prefix);
                if (subnet.Container) throw new ArgumentException("Assignments belong to a subnet, not a container prefix.");
                IPAddress ip = Address(assignment.Address);
                if (ip.ToString() != assignment.Address) throw new ArgumentException("Assignment addresses must be canonical.");
                if (!network.Contains(ip) || IpamNetwork.Number(ip) < network.FirstUsable || IpamNetwork.Number(ip) > network.LastUsable) throw new ArgumentException("Address is outside the usable subnet.");
                if (!assigned.Add(subnet.ViewId + "|" + ip)) throw new ArgumentException("Address is already assigned in this network view.");
                if (assignment.Status != "Assigned" && assignment.Status != "Reserved" && assignment.Status != "Deprecated") throw new ArgumentException("Status must be Assigned, Reserved, or Deprecated.");
            }
            if (state.Changes.Any(c => c is null || c.Revision < 1 || c.Revision > state.Revision)) throw new ArgumentException("Invalid IPAM change history.");
        }

        static IPAddress Address(string value)
        {
            if (!IPAddress.TryParse(value, out IPAddress ip) || ip.ScopeIdSafe() != 0 || ip.IsIPv4MappedToIPv6) throw new ArgumentException("Invalid IP address.");
            return ip;
        }
        static string Required(string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 255) throw new ArgumentException(field + " is required and must be at most 255 characters.");
            return value.Trim();
        }
        static string Text(string value)
        {
            if (value?.Length > 4096) throw new ArgumentException("Metadata must be at most 4096 characters.");
            return value?.Trim() ?? "";
        }

        T Mutate<T>(long revision, string actor, string operation, Func<IpamSnapshot, (IpamSnapshot State, T Result, string Id)> change)
        {
            lock (_sync)
            {
                if (revision != _state.Revision) throw new InvalidOperationException("IPAM changed; refresh inventory and retry with the current revision.");
                var next = change(_state);
                long newRevision = checked(_state.Revision + 1);
                IpamSnapshot updated = next.State with { Revision = newRevision, Changes = _state.Changes.Append(new IpamChange(newRevision, DateTime.UtcNow, Required(actor, "Actor"), operation, next.Id, ObjectJson(_state, next.Id), ObjectJson(next.State, next.Id))).ToArray() };
                Validate(updated);
                Save(updated);
                _state = updated;
                return next.Result;
            }
        }

        static string ObjectJson(IpamSnapshot state, string id)
        {
            object entity = (object)state.Views.SingleOrDefault(v => v.Id == id) ?? (object)state.Subnets.SingleOrDefault(s => s.Id == id) ?? state.Assignments.SingleOrDefault(a => a.Id == id);
            return entity is null ? null : JsonSerializer.Serialize(entity, entity.GetType(), JsonOptions);
        }

        void Save(IpamSnapshot state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path)));
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, state, JsonOptions); stream.Flush(true); }
                File.Move(temporary, _path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public IpamView PutView(long revision, string actor, string id, string name, string description) => Mutate(revision, actor, "view.set", state =>
        {
            if (!string.IsNullOrEmpty(id) && !state.Views.Any(v => v.Id == id)) throw new ArgumentException("Unknown network view.");
            IpamView view = new IpamView(string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id, Required(name, "Name"), Text(description));
            return (state with { Views = state.Views.Where(v => v.Id != view.Id).Append(view).ToArray() }, view, view.Id);
        });

        public IpamSubnet PutSubnet(long revision, string actor, string id, string viewId, string prefix, string name, string site, int? vlan, string dhcpScope, string dnsZone, bool container = false) => Mutate(revision, actor, "subnet.set", state =>
        {
            if (!string.IsNullOrEmpty(id) && !state.Subnets.Any(s => s.Id == id)) throw new ArgumentException("Unknown subnet.");
            if (container && (!string.IsNullOrEmpty(dhcpScope) || !string.IsNullOrEmpty(dnsZone))) throw new ArgumentException("Link services to subnets rather than containers.");
            IpamSubnet subnet = new IpamSubnet(string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id, viewId, new IpamNetwork(prefix).Prefix, Required(name, "Name"), Text(site), vlan, Text(dhcpScope), Text(dnsZone).TrimEnd('.').ToLowerInvariant(), container);
            return (state with { Subnets = state.Subnets.Where(s => s.Id != subnet.Id).Append(subnet).ToArray() }, subnet, subnet.Id);
        });

        public IpamAssignment PutAssignment(long revision, string actor, string id, string subnetId, string address, string hostName, string owner, string status, string description) => Mutate(revision, actor, "assignment.set", state =>
        {
            if (!string.IsNullOrEmpty(id) && !state.Assignments.Any(a => a.Id == id)) throw new ArgumentException("Unknown assignment.");
            IpamAssignment assignment = new IpamAssignment(string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id, subnetId, Address(address).ToString(), Text(hostName), Text(owner), status, Text(description));
            return (state with { Assignments = state.Assignments.Where(a => a.Id != assignment.Id).Append(assignment).ToArray() }, assignment, assignment.Id);
        });

        public void Delete(long revision, string actor, string kind, string id) => Mutate(revision, actor, kind + ".delete", state =>
        {
            IpamSnapshot next = kind switch
            {
                "view" when state.Views.Any(v => v.Id == id) && id != "default" && !state.Subnets.Any(s => s.ViewId == id) => state with { Views = state.Views.Where(v => v.Id != id).ToArray() },
                "subnet" when state.Subnets.Any(s => s.Id == id) && !state.Assignments.Any(a => a.SubnetId == id) && !HasChildPrefixes(state, id) => state with { Subnets = state.Subnets.Where(s => s.Id != id).ToArray() },
                "assignment" when state.Assignments.Any(a => a.Id == id) => state with { Assignments = state.Assignments.Where(a => a.Id != id).ToArray() },
                _ => throw new ArgumentException("Unknown object, protected default view, or object still has children.")
            };
            return (next, true, id);
        });

        static bool HasChildPrefixes(IpamSnapshot state, string id)
        {
            IpamSubnet subnet = state.Subnets.Single(s => s.Id == id);
            IpamNetwork network = new IpamNetwork(subnet.Prefix);
            return state.Subnets.Any(s => s.Id != id && s.ViewId == subnet.ViewId && new IpamNetwork(s.Prefix).PrefixLength > network.PrefixLength && network.Overlaps(new IpamNetwork(s.Prefix)));
        }

        public string NextAvailable(string subnetId, IEnumerable<string> observedAddresses)
        {
            IpamSnapshot state = Snapshot();
            IpamSubnet subnet = state.Subnets.SingleOrDefault(s => s.Id == subnetId) ?? throw new ArgumentException("Unknown subnet.");
            if (subnet.Container) throw new ArgumentException("Select a subnet rather than a container prefix.");
            IpamNetwork network = new IpamNetwork(subnet.Prefix);
            HashSet<BigInteger> occupied = state.Assignments.Where(a => a.SubnetId == subnetId).Select(a => IpamNetwork.Number(Address(a.Address))).ToHashSet();
            foreach (string address in observedAddresses)
                if (IPAddress.TryParse(address, out IPAddress ip) && network.Contains(ip)) occupied.Add(IpamNetwork.Number(ip));
            BigInteger candidate = network.FirstUsable;
            foreach (BigInteger used in occupied.Order())
            { if (used < candidate) continue; if (used > candidate) break; candidate++; }
            if (candidate > network.LastUsable) throw new InvalidOperationException("No available address remains in this subnet.");
            return network.Format(candidate);
        }

        public IpamFinding[] Reconcile(string subnetId, IEnumerable<IpamObservation> observations)
        {
            IpamSnapshot state = Snapshot();
            if (!state.Subnets.Any(s => s.Id == subnetId)) throw new ArgumentException("Unknown subnet.");
            var intended = state.Assignments.Where(a => a.SubnetId == subnetId).ToDictionary(a => a.Address);
            List<IpamFinding> findings = [];
            foreach (var group in observations.GroupBy(o => Address(o.Address).ToString()))
            {
                if (!intended.TryGetValue(group.Key, out IpamAssignment assignment)) findings.Add(new IpamFinding(group.Key, "Unmanaged", "Observed address has no IPAM assignment."));
                else if (!string.IsNullOrEmpty(assignment.HostName) && group.Any(o => !string.IsNullOrEmpty(o.HostName) && !string.Equals(o.HostName.TrimEnd('.'), assignment.HostName.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)))
                    findings.Add(new IpamFinding(group.Key, "HostNameDrift", "Observed hostname differs from intended hostname."));
                if (group.Where(o => o.Source == "DhcpLease").Select(o => o.ClientIdentifier).Distinct().Count() > 1) findings.Add(new IpamFinding(group.Key, "DuplicateLease", "Multiple DHCP client identifiers use this address."));
                intended.Remove(group.Key);
            }
            findings.AddRange(intended.Values.Select(a => new IpamFinding(a.Address, "Unobserved", "Assignment exists without current DNS or DHCP evidence; this does not prove the device is offline.")));
            return findings.ToArray();
        }
    }
}
