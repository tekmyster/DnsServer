var ddiNetBoxPreview = null;
var ddiDhcpNodes = [];
function ddiRequest(path, method, data, success) {
    HTTPRequest({ url: '/api/' + path, method: method, data: data || {}, token: sessionData.token, success: success, invalidToken: showPageLogin });
}
function ddiRefreshDhcp() {
    ddiRequest('ddi/dhcp/state/get', 'GET', {}, function (response) {
        var data = response.response, target = $('#ddiDhcpState').empty();
        ddiDhcpNodes = data.nodes || [data.node]; $('#ddiDhcpAddGroup').prop('disabled', ddiDhcpNodes.length < 2);
        if (!$('#ddiDhcpOwners').val()) $('#ddiDhcpOwners').val(ddiDhcpNodes.join(','));
        $('#ddiDhcpHealth').text(data.node + ': ' + data.health);
        $('#ddiDhcpBootstrap').toggle(sessionData.info.permissions.DhcpServer.canModify);
        data.state.plans.forEach(function (plan) {
            target.append($('<h5>').text(plan.scope + ' — epoch ' + plan.epoch + ' — owners: ' + plan.owners.join(', ')));
            var configuration = (data.state.configurations || []).find(function (c) { return c.scope === plan.scope; });
            var status = (data.scopeStatus || []).find(function (s) { return s.scope === plan.scope; });
            target.append($('<p>').text('Configuration revision ' + (configuration ? configuration.revision : 'bootstrap') + '; local listener ' + (status && status.enabled ? 'enabled' : 'disabled') + '; ownership ' + (status && status.matchesOwnership ? 'matches' : 'missing or drifted')));
            var list = $('<ul>');
            plan.owners.forEach(function (owner) {
                var replica = data.state.replicas.find(function (r) { return r.scope === plan.scope && r.node === owner; });
                list.append($('<li>').text(owner + ': ' + (replica ? 'sequence ' + replica.sequence + ', ' + replica.entries.filter(function (e) { return e.state === 'Lease'; }).length + ' lease records, ' + replica.entries.filter(function (e) { return e.state === 'Grant'; }).length + ' permanent donor holds' : 'no allocation snapshot received')));
            }); target.append(list);
        });
        if (!data.state.plans.length) target.append($('<p>').text('No distributed scopes configured.'));
    });
}
function ddiBootstrapDhcp() {
    var groups = [];
    $('#ddiDhcpGroupRows tr').each(function () { var row = $(this); groups.push({ name: row.find('.ddi-ha-name').val(), region: row.find('.ddi-ha-region').val(), primary: row.find('.ddi-ha-primary').val(), secondary: row.find('.ddi-ha-secondary').val() }); });
    ddiRequest('ddi/dhcp/plan/set', 'POST', { scope: $('#ddiDhcpScope').val(), owners: $('#ddiDhcpOwners').val(), haGroups: JSON.stringify(groups) }, ddiRefreshDhcp);
}
function ddiAddHaGroup() {
    var used = [];
    $('#ddiDhcpGroupRows select').each(function () { used.push($(this).val()); });
    var remaining = ddiDhcpNodes.filter(function (n) { return used.indexOf(n) < 0; }); if (remaining.length < 2) return;
    var row = $('<tr>'), count = $('#ddiDhcpGroupRows tr').length;
    row.append($('<td>').append($('<input>').attr('required', true).addClass('form-control ddi-ha-name').val(count ? 'Recovery ' + count : 'Local HA')));
    row.append($('<td>').append($('<input>').attr('required', true).addClass('form-control ddi-ha-region').val(count ? 'Recovery' : 'Local')));
    ['primary', 'secondary'].forEach(function (role, index) { var select = $('<select>').addClass('form-control ddi-ha-' + role); ddiDhcpNodes.forEach(function (n) { select.append($('<option>').val(n).text(n)); }); select.val(remaining[index]); row.append($('<td>').append(select)); });
    row.append($('<td>').append($('<button>').attr('type', 'button').addClass('btn btn-default').text('Remove').on('click', function () { row.remove(); }))); $('#ddiDhcpGroupRows').append(row);
}
function ddiLookupHistory() {
    ddiRequest('ddi/dhcp/history/lookup', 'GET', { address: $('#ddiHistoryAddress').val(), timestamp: $('#ddiHistoryTime').val() }, function (response) {
        var data = response.response, output = $('#ddiHistoryResult').empty();
        output.append($('<p>').text(data.device ? data.device.node + ' / ' + data.device.scope + ' / ' + data.device.lease.client + ' / ' + data.device.lease.hardware + ' / ' + (data.device.lease.host || '') : 'No unambiguous observation at that time.'));
        output.append($('<p>').text(data.coverage));
    });
}
function ddiLoadNetBox() {
    ddiRequest('ipam/netbox/settings/get', 'GET', {}, function (response) {
        var settings = response.response.settings;
        $('#ddiNetBoxSave').prop('disabled', false);
        $('#ddiNetBoxEnabled').prop('checked', settings.enabled); $('#ddiNetBoxUrl').val(settings.url); $('#ddiNetBoxCredential').val(settings.apiKeyEnvironment); $('#ddiNetBoxScheme').val(settings.tokenScheme);
        var body = $('#ddiNetBoxMappings').empty();
        (ipamInventory ? ipamInventory.views : []).forEach(function (view) {
            var mapping = settings.mappings.find(function (m) { return m.viewId === view.id; }), row = $('<tr>').attr('data-view', view.id);
            row.append($('<td>').append($('<input>').attr('type', 'checkbox').addClass('ddi-netbox-publish').prop('checked', !!mapping)));
            row.append($('<td>').text(view.name)); row.append($('<td>').append($('<input>').attr({ type: 'number', min: 1, placeholder: 'Global table' }).addClass('form-control ddi-netbox-vrf').val(mapping && mapping.vrfId != null ? mapping.vrfId : ''))); body.append(row);
        });
    });
}
function ddiSaveNetBox() {
    var mappings = [];
    $('#ddiNetBoxMappings tr').each(function () { var row = $(this), vrf = row.find('.ddi-netbox-vrf').val(); if (row.find('.ddi-netbox-publish').prop('checked')) mappings.push({ viewId: row.attr('data-view'), vrfId: vrf === '' ? null : Number(vrf) }); });
    var settings = { enabled: $('#ddiNetBoxEnabled').prop('checked'), url: $('#ddiNetBoxUrl').val(), apiKeyEnvironment: $('#ddiNetBoxCredential').val(), tokenScheme: $('#ddiNetBoxScheme').val(), mappings: mappings };
    ddiRequest('ipam/netbox/settings/set', 'POST', { settings: JSON.stringify(settings) }, function () { ddiNetBoxPreview = null; $('#ddiNetBoxApply').prop('disabled', true); ddiLoadNetBox(); });
}
function ddiPreviewNetBox() {
    $('#ddiNetBoxApply').prop('disabled', true);
    ddiRequest('ipam/netbox/preview', 'POST', {}, function (response) {
        ddiNetBoxPreview = response.response.preview;
        var output = $('#ddiNetBoxPreview').empty(), list = $('<ul>');
        output.append($('<p>').text('Inventory revision ' + ddiNetBoxPreview.inventoryRevision + '; preview ' + ddiNetBoxPreview.id));
        ddiNetBoxPreview.changes.forEach(function (c) {
            var fields = JSON.parse(c.fields), item = $('<li>').text(c.operation + ': ' + (fields.prefix || fields.address) + ' — ' + (fields.dns_name || fields.description || '') + ' — VRF ' + (fields.vrf == null ? 'global' : fields.vrf) + ' — ' + c.reason);
            if (c.operation === 'Update' && c.before) {
                var previous = JSON.parse(c.before), diff = $('<ul>');
                Object.keys(fields).forEach(function (name) {
                    var before = previous[name]; if (name === 'vrf' && before) before = before.id; if (name === 'status' && before && typeof before === 'object') before = before.value;
                    if (JSON.stringify(before) !== JSON.stringify(fields[name])) diff.append($('<li>').text(name + ': ' + JSON.stringify(before) + ' → ' + JSON.stringify(fields[name])));
                }); item.append(diff);
            }
            list.append(item);
        }); output.append(list);
        $('#ddiNetBoxApply').prop('disabled', ddiNetBoxPreview.changes.some(function (c) { return c.operation === 'Conflict'; }));
    });
}
function ddiApplyNetBox() {
    if (!ddiNetBoxPreview) return;
    $('#ddiNetBoxApply').prop('disabled', true);
    ddiRequest('ipam/netbox/apply', 'POST', { previewId: ddiNetBoxPreview.id }, function (response) {
        $('#ddiNetBoxPreview').append($('<p>').text(response.response.result.status + ': ' + response.response.result.applied + ' objects published.')); ddiNetBoxPreview = null;
    });
}
