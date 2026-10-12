using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DnsServerCore.Dhcp
{
    public sealed record AllocationRange(uint First, uint Last);
    public sealed record AllocationReservation(uint Address, string Client);
    public sealed record DhcpHaGroup(string Name, string Region, string Primary, string Secondary);
    public sealed record AllocationPlan(string Scope, string Epoch, uint First, uint Last, string Seed, string[] Owners, AllocationRange[] Excluded, uint Mask = 0, AllocationReservation[] Reservations = null, string Configuration = null, DhcpHaGroup[] HaGroups = null);
    public sealed record AllocationProtection(string State, DateTime Expires);
    public sealed record AllocationEntry(uint Address, string Client, string Hardware, string Host, string State, DateTime Expires, string GrantId = null, string Borrower = null, string AuthorityOwner = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AllocationProtection Protection = null);
    public sealed record AllocationEvent(long Sequence, DateTime Timestamp, AllocationEntry Before, AllocationEntry After);
    public sealed record AllocationReplica(string Scope, string Epoch, string Node, long Sequence, AllocationEntry[] Entries, AllocationEvent[] History = null);
    public sealed record AllocationDevice(string Scope, string Node, AllocationEntry Lease);
    public sealed record DistributedScopeConfiguration(string Scope, long Revision, string Hash, string Data);
    public sealed record AllocationState(int Version, AllocationPlan[] Plans, AllocationReplica[] Replicas, DistributedScopeConfiguration[] Configurations = null);
    public sealed record AllocationWatermark(string Node, AllocationWatermarkScope[] Scopes);
    public sealed record AllocationWatermarkScope(string Scope, string Epoch, long Sequence, string Hash);

    // A bijection, not hash modulo pool size: every eligible address has exactly one owner.
    public sealed class AllocationSequence
    {
        readonly AllocationRange[] _ranges;
        readonly string[] _owners;
        readonly ulong _multiplier, _offset, _inverse;
        readonly Dictionary<uint, string> _reserved = [];
        public ulong Count { get; }
        public AllocationSequence(AllocationPlan plan)
        {
            if (plan is null || string.IsNullOrWhiteSpace(plan.Scope) || plan.Scope.Length > 255 || !Guid.TryParseExact(plan.Epoch, "N", out _) || string.IsNullOrEmpty(plan.Seed) || plan.Seed.Length > 255 || plan.First > plan.Last || plan.Owners is null || plan.Owners.Length < 1 || plan.Owners.Length > 128 || plan.Owners.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 255) || plan.Owners.Distinct(StringComparer.Ordinal).Count() != plan.Owners.Length || plan.Excluded is null || plan.Excluded.Length > 10000)
                throw new ArgumentException("Invalid allocation plan.");
            _owners = plan.Owners.Order(StringComparer.Ordinal).ToArray();
            if (plan.HaGroups is not null && (plan.HaGroups.Length > 64 || plan.HaGroups.Any(g => g is null || string.IsNullOrWhiteSpace(g.Name) || g.Name.Length > 255 || string.IsNullOrWhiteSpace(g.Region) || g.Region.Length > 255 || g.Primary == g.Secondary || !_owners.Contains(g.Primary) || !_owners.Contains(g.Secondary)) || plan.HaGroups.Select(g => g.Name).Distinct().Count() != plan.HaGroups.Length || plan.HaGroups.SelectMany(g => new[] { g.Primary, g.Secondary }).Distinct().Count() != plan.HaGroups.Length * 2)) throw new ArgumentException("HA groups require distinct enrolled primary/secondary pairs; array order defines local then regional preference.");
            foreach (var reservation in plan.Reservations ?? [])
            {
                if (reservation.Address < plan.First || reservation.Address > plan.Last || string.IsNullOrWhiteSpace(reservation.Client) || reservation.Client.Length > 1024 || _reserved.ContainsKey(reservation.Address)) throw new ArgumentException("Invalid reservation.");
                byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(plan.Seed + ":" + reservation.Address));
                _reserved[reservation.Address] = _owners[System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(key) % _owners.Length];
            }
            List<AllocationRange> ranges = []; ulong cursor = plan.First;
            foreach (var excluded in plan.Excluded.Concat((plan.Reservations ?? []).Select(r => new AllocationRange(r.Address, r.Address))).OrderBy(r => r.First))
            {
                if (excluded.First > excluded.Last) throw new ArgumentException("Invalid exclusion.");
                if (excluded.Last < cursor || excluded.First > plan.Last) continue;
                if (excluded.First > cursor) ranges.Add(new((uint)cursor, excluded.First - 1));
                cursor = Math.Max(cursor, (ulong)excluded.Last + 1);
            }
            if (cursor <= plan.Last) ranges.Add(new((uint)cursor, plan.Last));
            _ranges = ranges.ToArray(); Count = _ranges.Aggregate(0UL, (n, r) => n + (ulong)r.Last - r.First + 1);
            if (Count == 0 && _reserved.Count > 0) { _multiplier = 1; _offset = 0; _inverse = 0; return; }
            if (Count < (ulong)_owners.Length) throw new ArgumentException("The pool needs at least one eligible address per owner.");
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(plan.Seed));
            _offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(0, 8)) % Count;
            ulong multiplier = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(8, 8)) % Count;
            if (multiplier == 0) multiplier = 1;
            while (BigInteger.GreatestCommonDivisor(multiplier, Count) != 1) multiplier = multiplier == Count - 1 ? 1 : multiplier + 1;
            _multiplier = multiplier;
            if (Count == 1) { _inverse = 0; return; }
            BigInteger oldR = Count, r = multiplier, oldT = 0, t = 1;
            while (r != 0) { BigInteger q = oldR / r; (oldR, r) = (r, oldR - q * r); (oldT, t) = (t, oldT - q * t); }
            _inverse = (ulong)((oldT % Count + Count) % Count);
        }
        uint At(ulong index)
        {
            // Product is at most (2^32-1)^2 and fits UInt64, but offset addition may overflow.
            ulong rank = (ulong)(((BigInteger)_multiplier * index + _offset) % Count);
            foreach (var range in _ranges)
            { ulong size = (ulong)range.Last - range.First + 1; if (rank < size) return (uint)(range.First + rank); rank -= size; }
            throw new InvalidOperationException("Invalid allocation rank.");
        }
        public string Owner(uint address)
        {
            if (_reserved.TryGetValue(address, out var reservedOwner)) return reservedOwner;
            ulong rank = 0;
            foreach (var range in _ranges)
            {
                if (address >= range.First && address <= range.Last)
                { rank += address - range.First; ulong input = Count == 1 ? 0 : (ulong)(((BigInteger)((rank + Count - _offset) % Count) * _inverse) % Count); return _owners[input % (ulong)_owners.Length]; }
                rank += (ulong)range.Last - range.First + 1;
            }
            return null;
        }
        public IEnumerable<uint> Candidates(string node)
        {
            int owner = Array.IndexOf(_owners, node); if (owner < 0) yield break;
            for (ulong index = (ulong)owner; index < Count; index += (ulong)_owners.Length) yield return At(index);
        }
        public static uint Number(IPAddress address)
        { if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new ArgumentException("DHCPv4 requires IPv4."); return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes()); }
        public static IPAddress Address(uint number)
        { byte[] bytes = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, number); return new(bytes); }
    }

    // Immutable ownership epochs allow independent service during partitions. Membership migration
    // needs a separate fenced handoff protocol; this store intentionally cannot replace an epoch.
    public sealed class DistributedAllocation : IAsyncDisposable
    {
        public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        readonly object _sync = new(); readonly string _path;
        readonly FileStream _writerLock;
        int _disposed;
        bool _recoveryRequired;
        public bool RecoveryRequired { get { lock (_sync) return _recoveryRequired; } }
        AllocationState _state;
        Dictionary<uint, AllocationDevice[]> _leaseIndex = [];
        Dictionary<string, AllocationSequence> _sequences = [];
        AllocationWatermark _watermark;
        AllocationState _verifiedAuthorityState;
        string _verifiedAuthorityNode;
        public string PersistedNode => Volatile.Read(ref _watermark)?.Node;
        readonly CancellationTokenSource _stop = new(); Task _worker;
        string _health = "Not started";
        public string Health { get { lock (_sync) return _recoveryRequired ? "Authoritative state requires offline recovery; DHCP service withheld" : _health; } }
        public Func<string> NodeName { get; set; } = () => "localhost";
        public DistributedAllocation(string path)
        {
            _path = path;
            if (!File.Exists(path) && File.Exists(path + ".required")) throw new InvalidDataException("Authoritative DHCP state is missing. Recover it before serving clients.");
            if (File.Exists(path + ".recovery-required")) throw new InvalidDataException("Peer evidence showed newer authoritative DHCP state. Offline recovery is required before serving.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            _writerLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                _state = File.Exists(path) ? JsonSerializer.Deserialize<AllocationState>(File.ReadAllBytes(path), JsonOptions) : new(1, [], []); Validate(_state);
                if (File.Exists(path + ".required"))
                { _watermark = JsonSerializer.Deserialize<AllocationWatermark>(File.ReadAllBytes(path + ".required"), JsonOptions) ?? throw new InvalidDataException("Invalid authoritative watermark."); ValidateWatermark(_state, _watermark); }
                else if (_state.Plans.Length > 0) throw new InvalidDataException("Authoritative watermark is missing; offline recovery is required.");
                RebuildLeaseIndex(); _sequences = _state.Plans.ToDictionary(p => p.Scope, p => new AllocationSequence(p));
            }
            catch { _writerLock.Dispose(); throw; }
        }
        static AllocationPlan Copy(AllocationPlan p) => p with { Owners = p.Owners.ToArray(), Excluded = p.Excluded.ToArray(), Reservations = p.Reservations?.ToArray(), HaGroups = p.HaGroups?.ToArray() };
        static AllocationReplica Copy(AllocationReplica r) => r with { Entries = r.Entries.ToArray(), History = r.History?.ToArray() };
        public AllocationState Snapshot() { lock (_sync) return _state with { Plans = _state.Plans.Select(Copy).ToArray(), Replicas = _state.Replicas.Select(Copy).ToArray(), Configurations = _state.Configurations?.ToArray() }; }
        public AllocationPlan Plan(string scope) { lock (_sync) { var p = _state.Plans.SingleOrDefault(p => p.Scope == scope); return p is null ? null : Copy(p); } }
        public bool ProtectedByOtherPlan(string scope, uint first, uint last)
        { lock (_sync) return _state.Plans.Any(p => p.Scope != scope && first <= p.Last && p.First <= last); }
        public byte[] Export() => JsonSerializer.SerializeToUtf8Bytes(Snapshot(), JsonOptions);
        static bool Same(AllocationPlan a, AllocationPlan b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions);
        static string AuthorityHash(AllocationPlan plan, AllocationReplica replica)
            => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { plan, replica }, JsonOptions)));
        static AllocationWatermark MakeWatermark(AllocationState state, string node)
            => new(node, state.Plans.Where(p => p.Owners.Contains(node)).Select(p =>
            {
                var replica = state.Replicas.SingleOrDefault(r => r.Scope == p.Scope && r.Node == node) ?? new(p.Scope, p.Epoch, node, 0, []);
                return new AllocationWatermarkScope(p.Scope, p.Epoch, replica.Sequence, AuthorityHash(p, replica));
            }).ToArray());
        static void ValidateWatermark(AllocationState state, AllocationWatermark watermark)
        {
            if (watermark.Node is null || watermark.Scopes is null) throw new InvalidDataException("Invalid authoritative watermark.");
            var actual = MakeWatermark(state, watermark.Node);
            foreach (var known in watermark.Scopes)
            {
                var present = actual.Scopes.SingleOrDefault(s => s.Scope == known.Scope);
                if (present is null || present.Epoch != known.Epoch || present.Sequence != known.Sequence || present.Hash != known.Hash) throw new InvalidDataException("Authoritative allocation state disagrees with its durable watermark; offline recovery required.");
            }
            if (actual.Scopes.Length != watermark.Scopes.Length) throw new InvalidDataException("Ownership differs from the durable watermark.");
        }
        void EnsureLocalAuthority()
        {
            if (_recoveryRequired) throw new InvalidOperationException("Authoritative DHCP state requires offline recovery.");
            if (_watermark is not null && _watermark.Scopes.Length > 0 && _watermark.Node != NodeName()) throw new InvalidOperationException("Allocation node identity changed; fenced migration is required.");
            if (ReferenceEquals(_verifiedAuthorityState, _state) && _verifiedAuthorityNode == NodeName()) return;
            if (_watermark is not null) ValidateWatermark(_state, _watermark);
            _verifiedAuthorityState = _state; _verifiedAuthorityNode = NodeName();
        }
        static void Validate(AllocationState state)
        {
            if (state is null || state.Version != 1 || state.Plans is null || state.Replicas is null || state.Plans.Length > 1000 || state.Replicas.Length > 128000 || state.Plans.Select(p => p.Scope).Distinct().Count() != state.Plans.Length) throw new ArgumentException("Invalid distributed state.");
            foreach (var p in state.Plans) _ = new AllocationSequence(p);
            if (state.Configurations?.Length > 1000 || state.Configurations?.Select(c => c.Scope).Distinct().Count() != state.Configurations?.Length || state.Configurations?.Any(c => c is null || !state.Plans.Any(p => p.Scope == c.Scope) || c.Revision < 1 || c.Data is null || c.Data.Length > 1024 * 1024 || Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(c.Data))) != c.Hash) == true) throw new ArgumentException("Invalid scope configuration snapshots.");
            foreach (var p in state.Plans) if (state.Plans.Any(other => other.Scope != p.Scope && p.First <= other.Last && other.First <= p.Last)) throw new ArgumentException("Distributed scopes cannot overlap.");
            if (state.Replicas.Select(r => (r.Scope, r.Node)).Distinct().Count() != state.Replicas.Length) throw new ArgumentException("Duplicate replicas.");
            foreach (var r in state.Replicas)
            {
                var p = state.Plans.SingleOrDefault(p => p.Scope == r.Scope) ?? throw new ArgumentException("Unknown scope.");
                var sequence = new AllocationSequence(p);
                if (r.Epoch != p.Epoch || !p.Owners.Contains(r.Node) || r.Sequence < 0 || r.Entries is null || r.Entries.Length > 100000 || r.Entries.Select(e => e.Address).Distinct().Count() != r.Entries.Length) throw new ArgumentException("Invalid replica.");
                if (r.History?.Length > 2000 || r.History?.Any(h => h is null || h.Sequence < 1 || h.Sequence > r.Sequence || h.Timestamp.Kind != DateTimeKind.Utc || h.After is null || h.After.Client?.Length > 1024 || h.After.Hardware?.Length > 255 || h.After.Host?.Length > 255 || h.After.Expires.Kind != DateTimeKind.Utc || h.After.State != "Offer" && h.After.State != "Lease" && h.After.State != "Grant" && h.After.State != "Released" && h.After.State != "Declined" || sequence.Owner(h.After.Address) != r.Node && h.After.AuthorityOwner != sequence.Owner(h.After.Address)) == true || r.History?.Select(h => h.Sequence).Distinct().Count() != r.History?.Length) throw new ArgumentException("Invalid lease history.");
                foreach (var e in r.Entries)
                {
                    if (e is null || e.Client?.Length > 1024 || e.Hardware?.Length > 255 || e.Host?.Length > 255 || e.Expires.Kind != DateTimeKind.Utc || e.State != "Offer" && e.State != "Lease" && e.State != "Grant" && e.State != "Released" && e.State != "Declined" || e.GrantId is not null && !Guid.TryParseExact(e.GrantId, "N", out _) || e.State == "Grant" && (e.GrantId is null || !p.Owners.Contains(e.Borrower) || e.Borrower == r.Node)) throw new ArgumentException("Invalid allocation entry.");
                    if (e.Protection is not null && (e.State != "Grant" || e.Protection.Expires.Kind != DateTimeKind.Utc || e.Protection.State != "Lease" && e.Protection.State != "Offer" && e.Protection.State != "Declined" || string.IsNullOrEmpty(e.Client))) throw new ArgumentException("Invalid handoff protection.");
                    if (sequence.Owner(e.Address) != r.Node)
                    {
                        var donor = state.Replicas.SingleOrDefault(d => d.Scope == r.Scope && d.Node == e.AuthorityOwner);
                        if (e.State == "Grant" || e.GrantId is null || e.AuthorityOwner != sequence.Owner(e.Address) || donor?.Entries.Any(g => g.State == "Grant" && g.Address == e.Address && g.GrantId == e.GrantId && g.Borrower == r.Node) != true) throw new ArgumentException("Missing durable donor authority.");
                    }
                    if (e.State != "Released" && (p.Reservations ?? []).Any(a => a.Address == e.Address && a.Client != e.Client)) throw new ArgumentException("Reserved address belongs to another client.");
                }
            }
        }
        void Save(AllocationState next)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_recoveryRequired) throw new InvalidOperationException("Local authoritative state requires offline recovery.");
            EnsureLocalAuthority();
            string temporary = _path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path)));
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, next, JsonOptions); stream.Flush(true); }
            if (next.Plans.Length > 0)
            {
                var watermark = MakeWatermark(next, NodeName());
                // Publish the barrier before the snapshot: a crash between these writes stops service,
                // rather than allowing the previous snapshot to allocate an already-advertised address.
                using (var marker = new FileStream(_path + ".required.tmp", FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(marker, watermark, JsonOptions); marker.Flush(true); }
                File.Move(_path + ".required.tmp", _path + ".required", true); _watermark = watermark; _verifiedAuthorityState = null;
            }
            File.Move(temporary, _path, true);
            _state = next;
            _verifiedAuthorityState = next; _verifiedAuthorityNode = NodeName();
            foreach (var plan in next.Plans) if (!_sequences.ContainsKey(plan.Scope)) _sequences.Add(plan.Scope, new AllocationSequence(plan));
            RebuildLeaseIndex();
        }
        public void AddPlan(AllocationPlan plan)
        {
            plan = Copy(plan with { Owners = plan.Owners.Order(StringComparer.Ordinal).ToArray(), Excluded = plan.Excluded.OrderBy(r => r.First).ThenBy(r => r.Last).ToArray() }); _ = new AllocationSequence(plan);
            lock (_sync)
            {
                var current = _state.Plans.SingleOrDefault(p => p.Scope == plan.Scope);
                if (current is not null) { if (Same(current, plan)) return; throw new InvalidOperationException("Ownership is immutable until fenced migration is implemented."); }
                var next = _state with { Plans = _state.Plans.Append(plan).ToArray() }; Validate(next); Save(next);
            }
        }
        public void Merge(byte[] bytes, bool importPlans = false)
        {
            var incoming = JsonSerializer.Deserialize<AllocationState>(bytes, JsonOptions); Validate(incoming);
            lock (_sync)
            {
                var plans = _state.Plans.ToDictionary(p => p.Scope);
                foreach (var p in incoming.Plans)
                {
                    if (plans.TryGetValue(p.Scope, out var existing)) { if (!Same(existing, p)) throw new InvalidOperationException("Conflicting ownership epoch."); }
                    else if (importPlans) plans.Add(p.Scope, Copy(p)); else throw new InvalidOperationException("Install the allocation plan before accepting replicas.");
                }
                var replicas = _state.Replicas.ToDictionary(r => (r.Scope, r.Node));
                foreach (var r in incoming.Replicas)
                {
                    if (r.Node == NodeName())
                    {
                        if (replicas.TryGetValue((r.Scope, r.Node), out var own) ? r.Sequence > own.Sequence || r.Sequence == own.Sequence && JsonSerializer.Serialize(r, JsonOptions) != JsonSerializer.Serialize(own, JsonOptions) : r.Sequence > 0)
                        {
                            using var marker = new FileStream(_path + ".recovery-required", FileMode.Create, FileAccess.Write, FileShare.None); marker.WriteByte(1); marker.Flush(true);
                            _recoveryRequired = true; _health = "Local authoritative state is older than peer evidence; DHCP service withheld pending offline recovery";
                            throw new InvalidOperationException(_health);
                        }
                        continue; // remote state never silently overwrites local authoritative allocation state
                    }
                    if (!replicas.TryGetValue((r.Scope, r.Node), out var current) || r.Sequence > current.Sequence) replicas[(r.Scope, r.Node)] = Copy(r);
                    else if (r.Sequence == current.Sequence && JsonSerializer.Serialize(r, JsonOptions) != JsonSerializer.Serialize(current, JsonOptions)) throw new InvalidOperationException("Conflicting replica sequence.");
                }
                var configurations = (_state.Configurations ?? []).ToDictionary(c => c.Scope);
                if (importPlans)
                    foreach (var configuration in incoming.Configurations ?? [])
                    {
                        if (!configurations.TryGetValue(configuration.Scope, out var current) || configuration.Revision > current.Revision) configurations[configuration.Scope] = configuration;
                        else if (configuration.Revision == current.Revision && configuration != current) throw new InvalidOperationException("Conflicting scope configuration revision.");
                    }
                var next = new AllocationState(1, plans.Values.ToArray(), replicas.Values.ToArray(), configurations.Values.ToArray());
                if (JsonSerializer.Serialize(next, JsonOptions) != JsonSerializer.Serialize(_state, JsonOptions)) Save(next);
            }
        }
        public AllocationState LocalSnapshot() => Snapshot(); // donor authorities accompany borrowed allocations
        public void PublishConfiguration(string scope, byte[] data)
        {
            if (data.Length > 768 * 1024) throw new ArgumentException("Scope configuration exceeds 768 KiB.");
            lock (_sync)
            {
                if (!_state.Plans.Any(p => p.Scope == scope)) throw new ArgumentException("Unknown distributed scope.");
                string hash = Convert.ToHexString(SHA256.HashData(data)); var current = (_state.Configurations ?? []).SingleOrDefault(c => c.Scope == scope);
                if (current?.Hash == hash) return;
                var next = new DistributedScopeConfiguration(scope, checked((current?.Revision ?? 0) + 1), hash, Convert.ToBase64String(data));
                Save(_state with { Configurations = (_state.Configurations ?? []).Where(c => c.Scope != scope).Append(next).ToArray() });
            }
        }
        AllocationReplica Local(string scope, AllocationPlan p) => _state.Replicas.SingleOrDefault(r => r.Scope == scope && r.Node == NodeName()) ?? new(scope, p.Epoch, NodeName(), 0, []);
        void Mutate(string scope, AllocationEntry entry)
        {
            var plan = _state.Plans.Single(p => p.Scope == scope); var local = Local(scope, plan);
            if (!HasAuthority(scope, entry.Address)) throw new InvalidOperationException("Address belongs to a different owner.");
            // Released tombstones can be compacted: every replica is a complete snapshot and sequence is durable.
            var entries = local.Entries.Where(e => e.Address != entry.Address && (e.State == "Grant" || e.State != "Released" && e.Expires > DateTime.UtcNow || IncomingGrant(scope, e.Address)?.Protection?.Expires > DateTime.UtcNow)).Append(entry).ToArray();
            long number = checked(local.Sequence + 1);
            var history = (local.History ?? []).Append(new AllocationEvent(number, DateTime.UtcNow, local.Entries.SingleOrDefault(e => e.Address == entry.Address), entry)).TakeLast(2000).ToArray();
            var next = local with { Sequence = number, Entries = entries, History = history };
            Save(_state with { Replicas = _state.Replicas.Where(r => r.Scope != scope || r.Node != next.Node).Append(next).ToArray() });
        }
        public bool Available(string scope, uint address, string client = null)
        {
            lock (_sync)
            {
                if (_recoveryRequired) return false;
                EnsureLocalAuthority();
                var p = _state.Plans.Single(p => p.Scope == scope);
                if ((p.Reservations ?? []).Any(r => r.Address == address && r.Client != client)) return false;
                if (!HasAuthority(scope, address)) return false;
                var protection = IncomingGrant(scope, address);
                if (protection?.Protection?.Expires > DateTime.UtcNow && (protection.Protection.State == "Declined" || client is null || protection.Client != client)) return false;
                var entry = Local(scope, p).Entries.SingleOrDefault(e => e.Address == address);
                return entry is null || entry.State == "Released" || entry.State != "Grant" && (entry.Expires <= DateTime.UtcNow || entry.State != "Declined" && client is not null && entry.Client == client);
            }
        }
        public bool Hold(string scope, uint address, string client, string hardware, string host, DateTime expires)
        {
            lock (_sync)
            {
                if (!Available(scope, address, client)) return false;
                Mutate(scope, Entry(scope, address, client, hardware, host, "Offer", expires)); return true;
            }
        }
        public void Commit(string scope, uint address, string client, string hardware, string host, DateTime expires)
        {
            lock (_sync)
            {
                if (!Available(scope, address, client)) throw new InvalidOperationException("Allocation authority is unavailable.");
                var prior = Local(scope, _state.Plans.Single(p => p.Scope == scope)).Entries.SingleOrDefault(e => e.Address == address && e.State == "Lease" && e.Client == client);
                if (prior is not null && prior.Expires > expires) expires = prior.Expires; // a lost shorter renewal ACK cannot revoke the client's prior lifetime
                var inherited = IncomingGrant(scope, address);
                if (inherited?.Protection?.State == "Lease" && inherited.Protection.Expires > expires) expires = inherited.Protection.Expires;
                Mutate(scope, Entry(scope, address, client, hardware, host, "Lease", expires));
            }
        }
        public void Release(string scope, uint address, string client)
        {
            lock (_sync)
            {
                var p = _state.Plans.Single(p => p.Scope == scope); var entry = Local(scope, p).Entries.SingleOrDefault(e => e.Address == address);
                entry ??= InheritedEntries(scope, NodeName(), "Lease").Concat(InheritedEntries(scope, NodeName(), "Offer")).SingleOrDefault(e => e.Address == address);
                if (entry is null || entry.Client != client || entry.State == "Grant") return;
                Mutate(scope, entry with { State = "Released", Expires = DateTime.UtcNow });
            }
        }
        // A donor grant never expires on its own. A lost response leaves the address held; an idempotent
        // retry returns the same authority. Transfer back requires a future explicit drain/return protocol.
        public AllocationEntry Grant(string scope, uint address, string borrower, string requestId)
        {
            if (!Guid.TryParseExact(requestId, "N", out _)) throw new ArgumentException("Invalid grant request ID.");
            lock (_sync)
            {
                var p = _state.Plans.Single(p => p.Scope == scope);
                if (_sequences[scope].Owner(address) != NodeName()) throw new InvalidOperationException("Only the original owner can grant an address.");
                if ((p.Reservations ?? []).Any(r => r.Address == address)) throw new InvalidOperationException("Reserved addresses cannot be borrowed.");
                if (borrower == NodeName() || !p.Owners.Contains(borrower)) throw new ArgumentException("Unknown borrower.");
                var old = Local(scope, p).Entries.SingleOrDefault(e => e.GrantId == requestId);
                if (old is not null) { if (old.Address != address || old.Borrower != borrower) throw new InvalidOperationException("Grant request changed."); return old; }
                if (!Available(scope, address)) throw new InvalidOperationException("Donor address is already held.");
                var grant = new AllocationEntry(address, "", "", "", "Grant", DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), requestId, borrower);
                Mutate(scope, grant); return grant;
            }
        }
        // Connected-owner surrender is atomic with the existing ACK lock. A lost reply must never
        // restore donor authority; retry the same operation or recover the published grant snapshot.
        public void Handoff(string scope, uint[] addresses, string borrower, string requestId, long expectedSequence)
        {
            if (!Guid.TryParseExact(requestId, "N", out _) || addresses is null || addresses.Length < 1 || addresses.Length > 256 || addresses.Distinct().Count() != addresses.Length) throw new ArgumentException("Use a stable request ID and 1–256 distinct addresses.");
            lock (_sync)
            {
                EnsureLocalAuthority();
                var plan = _state.Plans.Single(p => p.Scope == scope); var local = Local(scope, plan);
                if (borrower == NodeName() || !plan.Owners.Contains(borrower)) throw new ArgumentException("Receiver must be another enrolled owner.");
                string Id(uint address) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(requestId + ":" + address.ToString(System.Globalization.CultureInfo.InvariantCulture))).AsSpan(0, 16)).ToString("N");
                bool Retried(uint address) => local.Entries.Any(e => e.Address == address && e.State == "Grant" && e.GrantId == Id(address) && e.Borrower == borrower);
                if (addresses.All(Retried)) return;
                if (local.Sequence != expectedSequence) throw new InvalidOperationException("Owner state changed; review a fresh snapshot before handoff.");
                var entries = local.Entries.ToDictionary(e => e.Address); var history = (local.History ?? []).ToList(); long sequence = local.Sequence;
                foreach (uint address in addresses.Order())
                {
                    if (_sequences[scope].Owner(address) != NodeName()) throw new InvalidOperationException("Only the original owner can surrender an address.");
                    entries.TryGetValue(address, out var previous);
                    if (previous?.State == "Grant") throw new InvalidOperationException("Address already has a permanent receiver.");
                    var reservation = (plan.Reservations ?? []).SingleOrDefault(r => r.Address == address);
                    var active = previous?.Expires > DateTime.UtcNow && previous.State != "Released";
                    var grant = new AllocationEntry(address, active ? previous.Client : reservation?.Client ?? "", active ? previous.Hardware : "", active ? previous.Host : "", "Grant", DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), Id(address), borrower,
                        Protection: active ? new(previous.State, previous.Expires) : null);
                    entries[address] = grant; history.Add(new(checked(++sequence), DateTime.UtcNow, previous, grant));
                }
                var next = local with { Sequence = sequence, Entries = entries.Values.ToArray(), History = history.TakeLast(2000).ToArray() };
                var state = _state with { Replicas = _state.Replicas.Where(r => r.Scope != scope || r.Node != next.Node).Append(next).ToArray() };
                Validate(state); Save(state);
            }
        }
        AllocationEntry IncomingGrant(string scope, uint address)
        {
            string owner = _sequences[scope].Owner(address);
            return owner == NodeName() ? null : _state.Replicas.SingleOrDefault(r => r.Scope == scope && r.Node == owner)?.Entries.SingleOrDefault(e => e.Address == address && e.State == "Grant" && e.Borrower == NodeName());
        }
        AllocationEntry Entry(string scope, uint address, string client, string hardware, string host, string state, DateTime expires)
        {
            var p = _state.Plans.Single(p => p.Scope == scope); string owner = _sequences[scope].Owner(address);
            var grant = owner == NodeName() ? null : _state.Replicas.SingleOrDefault(r => r.Scope == scope && r.Node == owner)?.Entries.SingleOrDefault(e => e.Address == address && e.State == "Grant" && e.Borrower == NodeName());
            return new(address, client, hardware, host, state, expires, grant?.GrantId, null, grant is null ? null : owner);
        }
        public bool HasAuthority(string scope, uint address)
        {
            lock (_sync)
            {
                var p = _state.Plans.Single(p => p.Scope == scope); string owner = _sequences[scope].Owner(address);
                if (owner == NodeName()) return Local(scope, p).Entries.All(e => e.Address != address || e.State != "Grant");
                return owner is not null && _state.Replicas.SingleOrDefault(r => r.Scope == scope && r.Node == owner)?.Entries.Any(e => e.Address == address && e.State == "Grant" && e.Borrower == NodeName()) == true;
            }
        }
        public uint[] GrantedAddresses(string scope)
        {
            lock (_sync) return _state.Replicas.Where(r => r.Scope == scope && r.Node != NodeName()).SelectMany(r => r.Entries).Where(e => e.State == "Grant" && e.Borrower == NodeName()).Select(e => e.Address).ToArray();
        }
        public AllocationEntry FindLease(string scope, string client)
        {
            lock (_sync)
            {
                var entries = _state.Replicas.Where(r => r.Scope == scope && r.Node == NodeName()).SelectMany(r => r.Entries).ToArray();
                return entries.Where(e => e.Client == client && e.State == "Lease" && e.Expires > DateTime.UtcNow)
                    .Concat(_state.Replicas.Where(r => r.Scope == scope && r.Node != NodeName()).SelectMany(r => r.Entries)
                        .Where(e => e.State == "Grant" && e.Borrower == NodeName() && e.Client == client && e.Protection?.State == "Lease" && e.Protection.Expires > DateTime.UtcNow && !entries.Any(l => l.Address == e.Address))
                        .Select(e => Entry(scope, e.Address, e.Client, e.Hardware, e.Host, "Lease", e.Protection.Expires)))
                    .OrderByDescending(e => e.Expires).FirstOrDefault();
            }
        }
        public AllocationEntry FindOffer(string scope, string client)
        { lock (_sync) return _state.Replicas.Where(r => r.Scope == scope && r.Node == NodeName()).SelectMany(r => r.Entries).Concat(InheritedEntries(scope, NodeName(), "Offer")).Where(e => e.Client == client && e.State == "Offer" && e.Expires > DateTime.UtcNow).OrderByDescending(e => e.Expires).FirstOrDefault(); }
        public void ReleaseOffer(string scope, string client)
        { lock (_sync) { var offer = FindOffer(scope, client); if (offer is not null) Mutate(scope, offer with { State = "Released", Expires = DateTime.UtcNow }); } }
        public bool HasLiveOffer(string scope, uint address, string client)
        { lock (_sync) return FindOffer(scope, client)?.Address == address; }
        public void Decline(string scope, uint address, string client)
        {
            lock (_sync)
            {
                var p = _state.Plans.Single(p => p.Scope == scope); var entry = Local(scope, p).Entries.SingleOrDefault(e => e.Address == address && e.Client == client);
                entry ??= InheritedEntries(scope, NodeName(), "Lease").Concat(InheritedEntries(scope, NodeName(), "Offer")).SingleOrDefault(e => e.Address == address && e.Client == client);
                if (entry is not null && entry.State != "Grant") Mutate(scope, entry with { State = "Declined", Expires = DateTime.UtcNow.AddMinutes(10) });
            }
        }
        IEnumerable<AllocationEntry> InheritedEntries(string scope, string receiver, string state)
        {
            var local = _state.Replicas.SingleOrDefault(r => r.Scope == scope && r.Node == receiver);
            return _state.Replicas.Where(r => r.Scope == scope && r.Node != receiver).SelectMany(r => r.Entries
                .Where(e => e.State == "Grant" && e.Borrower == receiver && e.Protection?.State == state && local?.Entries.Any(l => l.Address == e.Address) != true)
                .Select(e => e with { State = state, Expires = e.Protection.Expires, Borrower = null, AuthorityOwner = r.Node, Protection = null }));
        }
        void RebuildLeaseIndex()
        {
            var receivers = _state.Replicas.ToDictionary(r => (r.Scope, r.Node), r => r.Entries.Select(e => e.Address).ToHashSet());
            var leases = _state.Replicas.SelectMany(r => r.Entries.Where(e => e.State == "Lease").Select(e => new AllocationDevice(r.Scope, r.Node, e)))
                .Concat(_state.Replicas.SelectMany(r => r.Entries.Where(e => e.State == "Grant" && e.Protection?.State == "Lease" && (!receivers.TryGetValue((r.Scope, e.Borrower), out var known) || !known.Contains(e.Address)))
                    .Select(e => new AllocationDevice(r.Scope, e.Borrower, e with { State = "Lease", Expires = e.Protection.Expires, Borrower = null, AuthorityOwner = r.Node, Protection = null }))));
            Volatile.Write(ref _leaseIndex, leases.GroupBy(d => d.Lease.Address).ToDictionary(g => g.Key, g => g.ToArray()));
        }
        public AllocationDevice ResolveLive(uint address)
        {
            if (!Volatile.Read(ref _leaseIndex).TryGetValue(address, out var values)) return null;
            var active = values.Where(d => d.Lease.Expires > DateTime.UtcNow).ToArray();
            return active.Length == 1 ? active[0] : null; // immutable index lookup never waits for replication/disk writes
        }
        public AllocationDevice[] LiveLeases(string scope)
            => Volatile.Read(ref _leaseIndex).Values.SelectMany(d => d).Where(d => d.Scope == scope && d.Lease.Expires > DateTime.UtcNow).ToArray();
        public bool HasLiveConflict(uint address, string client, string hardware)
        { return Volatile.Read(ref _leaseIndex).TryGetValue(address, out var values) && values.Any(d => d.Lease.Expires > DateTime.UtcNow && (d.Lease.Client != client || !string.Equals(d.Lease.Hardware, hardware, StringComparison.OrdinalIgnoreCase))); }
        public AllocationDevice ResolveAt(uint address, DateTime timestamp)
        {
            lock (_sync)
            {
                var latest = _state.Replicas.Select(r => (r, history: (r.History ?? []).Where(h => h.After.Address == address && h.Timestamp <= timestamp).OrderByDescending(h => h.Sequence).FirstOrDefault())).Where(x => x.history is not null).ToArray();
                var observations = latest.Where(x => x.history.After.State == "Lease" && x.history.After.Expires > timestamp).Select(x => new AllocationDevice(x.r.Scope, x.r.Node, x.history.After))
                    .Concat(latest.Where(x => x.history.After.State == "Grant" && x.history.After.Protection?.State == "Lease" && x.history.After.Protection.Expires > timestamp && !latest.Any(other => other.r.Scope == x.r.Scope && other.r.Node == x.history.After.Borrower && other.history.Timestamp >= x.history.Timestamp))
                        .Select(x => new AllocationDevice(x.r.Scope, x.history.After.Borrower, x.history.After with { State = "Lease", Expires = x.history.After.Protection.Expires, Borrower = null, AuthorityOwner = x.r.Node, Protection = null }))).ToArray();
                return observations.Length == 1 ? observations[0] : null;
            }
        }
        public void Start(Func<CancellationToken, Task> synchronize)
        {
            lock (_sync) _worker ??= Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try { await synchronize(_stop.Token); lock (_sync) _health = "Synchronization pass completed; inspect per-owner sequences for applied state"; }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                    catch (Exception) { lock (_sync) _health = "Synchronization failed; authoritative local allocations retained, retry scheduled"; }
                    try { await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token); } catch (OperationCanceledException) { break; }
                }
            });
        }
        public async ValueTask DisposeAsync()
        { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; _stop.Cancel(); try { if (_worker is not null) await _worker; } finally { _stop.Dispose(); _writerLock.Dispose(); } }
    }
}
