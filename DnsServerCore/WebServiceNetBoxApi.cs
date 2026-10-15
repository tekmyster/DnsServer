using DnsServerCore.Auth;
using DnsServerCore.Cluster;
using DnsServerCore.Ipam;
using Microsoft.AspNetCore.Http;
using System;
using System.Text.Json;
using System.Threading.Tasks;
using TechnitiumLibrary;

namespace DnsServerCore
{
    public partial class DnsWebService
    {
        readonly NetBoxConnector _netbox;
        void RequireNetBoxAdmin(HttpContext context)
        {
            if (!_authManager.IsPermitted(PermissionSection.Administration, GetSessionUser(context), PermissionFlag.Modify) || !_authManager.IsPermitted(PermissionSection.Settings, GetSessionUser(context), PermissionFlag.Modify)) throw new DnsWebServiceException("Administration and Settings modify permissions are required.");
            if (_clusterManager.ClusterInitialized && _clusterManager.GetSelfNode().Type != ClusterNodeType.Primary) throw new DnsWebServiceException("NetBox integration is owned by the cluster primary.");
        }
        static void WriteNetBox(HttpContext context, string name, object value)
        { var writer = context.GetCurrentJsonWriter(); writer.WritePropertyName(name); JsonSerializer.Serialize(writer, value, value.GetType(), NetBoxConnector.JsonOptions); }
        void GetNetBoxSettings(HttpContext context)
        { RequireNetBoxAdmin(context); WriteNetBox(context, "settings", _netbox.Settings); }
        async Task SetNetBoxSettings(HttpContext context)
        {
            RequireNetBoxAdmin(context); string json = context.Request.GetQueryOrForm("settings");
            if (json.Length > 64 * 1024) throw new ArgumentException("Integration settings exceed 64 KiB.");
            var settings = JsonSerializer.Deserialize<NetBoxSettings>(json, NetBoxConnector.JsonOptions) ?? throw new ArgumentException("Missing settings.");
            if (settings.Mappings is null || Array.Exists(settings.Mappings, m => !Array.Exists(_ipam.Snapshot().Views, v => v.Id == m.ViewId))) throw new ArgumentException("Mapping references an unknown network view.");
            await _netbox.ConfigureAsync(settings, context.RequestAborted); WriteNetBox(context, "settings", _netbox.Settings);
            _log.Write(GetRemoteEndPoint(context), "[" + GetSessionUser(context).Username + "] Updated NetBox integration settings.");
        }
        async Task PreviewNetBox(HttpContext context)
        { RequireNetBoxAdmin(context); WriteNetBox(context, "preview", await _netbox.PreviewAsync(_ipam.Snapshot(), context.RequestAborted)); }
        async Task ApplyNetBox(HttpContext context)
        {
            RequireNetBoxAdmin(context);
            WriteNetBox(context, "result", await _netbox.ApplyAsync(context.Request.GetQueryOrForm("previewId"), _ipam.Snapshot().Revision, context.RequestAborted, () => _ipam.Snapshot().Revision));
            _log.Write(GetRemoteEndPoint(context), "[" + GetSessionUser(context).Username + "] Applied a reviewed NetBox publication.");
        }
    }
}
