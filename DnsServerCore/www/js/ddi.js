var ddiNetBoxPreview = null;
var ddiDhcpNodes = [];
function ddiRequest(path, method, data, success, error, alerts) {
    HTTPRequest({ url: '/api/' + path, method: method, data: data || {}, token: sessionData.token, success: success, error: error, objAlertPlaceholder: alerts, invalidToken: showPageLogin });
}
function ddiAddress(number) { return [24, 16, 8, 0].map(function (shift) { return (number >>> shift) & 255; }).join('.'); }
function ddiLeaseState(state) { return { Lease: 'Active lease', Offer: 'Pending offer', Declined: 'Quarantined', Grant: 'Transferred', Released: 'Released' }[state] || 'Unknown'; }
function ddiActiveLeaseCount(data, scope, owner) {
    var local = data.state.replicas.find(function (r) { return r.scope === scope && r.node === owner; }), entries = local ? local.entries : [], count = entries.filter(function (e) { return e.state === 'Lease' && new Date(e.expires) > new Date(); }).length;
    data.state.replicas.filter(function (r) { return r.scope === scope && r.node !== owner; }).forEach(function (r) {
        count += r.entries.filter(function (e) { return e.state === 'Grant' && e.borrower === owner && e.protection && e.protection.state === 'Lease' && new Date(e.protection.expires) > new Date() && !entries.some(function (own) { return own.address === e.address; }); }).length;
    }); return count;
}
function ddiRefreshDhcp() {
    $('#ddiDhcpHealth').text('Loading DHCP status…');
    ddiRequest('ddi/dhcp/state/get', 'GET', {}, function (response) {
        var data = response.response, target = $('#ddiDhcpState').empty();
        ddiDhcpNodes = data.nodes || [data.node]; $('#ddiDhcpAddGroup').prop('disabled', ddiDhcpNodes.length < 2);
        if (!$('#ddiDhcpOwners').val()) $('#ddiDhcpOwners').val(ddiDhcpNodes.join(','));
        var health = data.health.indexOf('recovery') >= 0 || data.health.indexOf('Recovery') >= 0 ? 'Recovery required' : data.health.indexOf('failed') >= 0 ? 'Sync interrupted' : data.health.indexOf('completed') >= 0 ? 'Last sync completed' : 'Sync not started';
        $('#ddiDhcpHealth').text(data.node + ' · ' + health);
        $('#ddiDhcpBootstrap').toggle(sessionData.info.permissions.DhcpServer.canModify);
        data.state.plans.forEach(function (plan) {
            target.append($('<h4>').text(plan.scope));
            var configuration = (data.state.configurations || []).find(function (c) { return c.scope === plan.scope; });
            var status = (data.scopeStatus || []).find(function (s) { return s.scope === plan.scope; });
            target.append($('<p>').text(status && status.matchesOwnership ? (status.enabled ? 'Serving on this server' : 'Stopped on this server') : 'Configuration needs attention. Restore the scope to its shared address settings before serving.'));
            var table = $('<table>').addClass('table table-condensed'), body = $('<tbody>'), diagnostics = $('<details>').append($('<summary>').text('Synchronization details'));
            table.append($('<thead>').append($('<tr>').append($('<th>').text('Server')).append($('<th>').text('Active leases')).append($('<th>').text('Transferred addresses')))).append(body);
            diagnostics.append($('<p>').text('Ownership version: ' + plan.epoch + ' · Settings revision: ' + (configuration ? configuration.revision : 'initial')));
            plan.owners.forEach(function (owner) {
                var replica = data.state.replicas.find(function (r) { return r.scope === plan.scope && r.node === owner; });
                var activeCount = ddiActiveLeaseCount(data, plan.scope, owner);
                body.append($('<tr>').append($('<td>').text(owner)).append($('<td>').text(replica || activeCount ? activeCount : 'Awaiting sync')).append($('<td>').text(replica ? replica.entries.filter(function (e) { return e.state === 'Grant'; }).length : '—')));
                diagnostics.append($('<p>').text(owner + ' · Last change: ' + (replica ? replica.sequence : 'not received')));
            }); target.append($('<div>').addClass('table-responsive').append(table)).append(diagnostics);
            if (sessionData.info.permissions.DhcpServer.canModify) ddiRenderHandoff(target, plan, data);
        });
        if (!data.state.plans.length) target.append($('<p>').text('No shared scopes yet. Set up a scope to synchronize DHCP across servers.'));
    }, function () { $('#ddiDhcpHealth').text('Status unavailable. Refresh to try again.'); });
}
function ddiRenderHandoff(target, plan, data) {
    if (plan.owners.length < 2) return;
    var local = data.state.replicas.find(function (r) { return r.scope === plan.scope && r.node === data.node; });
    if (!local) return;
    var transferable = local.entries.filter(function (e) { return !e.authorityOwner && ['Lease', 'Offer', 'Declined'].indexOf(e.state) >= 0 && new Date(e.expires) > new Date(); }).slice(0, 256);
    if (!transferable.length) return;
    var details = $('<details>').css('margin-top', '15px').append($('<summary>').text('Transfer addresses'));
    var form = $('<form>'), receiver = $('<select>').addClass('form-control'), choices = $('<div>'), output = $('<p>').attr({ role: 'status', 'aria-live': 'polite' }), button = $('<button>').attr('type', 'submit').addClass('btn btn-primary').text('Review transfer'), request = null, reviewed = false;
    var review = $('<div>').hide(), back = $('<button>').attr('type', 'button').addClass('btn btn-default').text('Back').hide(), alerts = $('<div>'), technical = $('<details>').append($('<summary>').text('Technical details')).append(alerts).hide();
    plan.owners.filter(function (n) { return n !== data.node; }).forEach(function (n) { receiver.append($('<option>').val(n).text(n)); });
    form.append($('<p>').text('Move selected addresses to another server. Existing clients keep their lease expiry.'));
    var table = $('<table>').addClass('table table-hover table-condensed'), body = $('<tbody>');
    table.append($('<thead>').append($('<tr>').append($('<th>').text('Select')).append($('<th>').text('Address')).append($('<th>').text('Device')).append($('<th>').text('Status')).append($('<th>').text('Expires')))).append(body);
    transferable.forEach(function (e) { var address = ddiAddress(e.address); body.append($('<tr>').append($('<td>').append($('<input>').attr({ type: 'checkbox', value: address, 'aria-label': 'Select ' + address }))).append($('<td>').text(address)).append($('<td>').text(e.host || e.hardware || e.client)).append($('<td>').text(ddiLeaseState(e.state))).append($('<td>').text(new Date(e.expires).toLocaleString()))); });
    choices.append($('<div>').addClass('table-responsive').append(table));
    var receiverLabel = $('<label>').text('Receiving server').css('display', 'block').append(receiver);
    form.append(choices).append($('<div>').addClass('form-group').css('max-width', '360px').append(receiverLabel)).append(review).append(button).append(document.createTextNode(' ')).append(back).append(output).append(technical);
    back.on('click', function () { reviewed = false; request = null; review.hide(); back.hide(); choices.find('input').prop('disabled', false); receiver.prop('disabled', false); button.text('Review transfer').trigger('focus'); });
    form.on('submit', function (event) {
        event.preventDefault();
        if (!request) {
            var addresses = choices.find('input:checked').map(function () { return this.value; }).get();
            if (!addresses.length) { output.text('Select at least one address.'); return; }
            var bytes = new Uint8Array(16); window.crypto.getRandomValues(bytes);
            request = { scope: plan.scope, addresses: addresses.join(','), receiver: receiver.val(), expectedSequence: local.sequence, requestId: Array.from(bytes).map(function (b) { return b.toString(16).padStart(2, '0'); }).join('') };
            choices.find('input').prop('disabled', true); receiver.prop('disabled', true);
        }
        if (!reviewed) {
            reviewed = true;
            var count = request.addresses.split(',').length;
            review.empty().show().append($('<p>').text(count + (count === 1 ? ' address will' : ' addresses will') + ' move from ' + data.node + ' to ' + request.receiver + '.')).append($('<p>').addClass('text-warning').text('This server will stop assigning these addresses. Returning them is not yet supported.'));
            back.show(); button.text('Transfer addresses').trigger('focus'); output.text(''); return;
        }
        back.hide(); button.prop('disabled', true).text('Transferring…'); output.text('Waiting for both servers to confirm the transfer.');
        ddiRequest('ddi/dhcp/handoff', 'POST', request, function (response) {
            review.hide();
            if (response.response.receiverAcknowledged) { output.text('Transfer complete. Refresh DHCP status to see the updated addresses.'); button.text('Transferred'); }
            else { output.text('Addresses released by this server. The receiving server has not confirmed; retry delivery when its connection returns.'); button.prop('disabled', false).text('Retry delivery'); }
        }, function () {
            technical.show();
            var stale = alerts.text().indexOf('Owner state changed') >= 0;
            output.text(stale ? 'The lease list changed. Refresh DHCP status before trying again.' : 'Transfer could not be confirmed. Retry the same transfer when the connection returns.');
            button.prop('disabled', stale).text(stale ? 'Refresh required' : 'Retry transfer');
        }, alerts);
    }); details.append(form); target.append(details);
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
        output.append($('<p>').text(ddiNetBoxPreview.changes.length + ' objects reviewed.'));
        output.append($('<details>').append($('<summary>').text('Publication details')).append($('<p>').text('Inventory revision: ' + ddiNetBoxPreview.inventoryRevision + ' · Preview reference: ' + ddiNetBoxPreview.id)));
        ddiNetBoxPreview.changes.forEach(function (c) {
            var fields = JSON.parse(c.fields), item = $('<li>').text(c.operation + ': ' + (fields.prefix || fields.address) + ' — ' + (fields.dns_name || fields.description || '') + ' — VRF ' + (fields.vrf == null ? 'global' : fields.vrf) + ' — ' + c.reason);
            if (c.operation === 'Update' && c.before) {
                var previous = JSON.parse(c.before), diff = $('<ul>');
                Object.keys(fields).forEach(function (name) {
                    var before = previous[name]; if (name === 'vrf' && before) before = before.id; if (name === 'status' && before && typeof before === 'object') before = before.value;
                    var labels = { prefix: 'Prefix', address: 'Address', vrf: 'VRF', status: 'Status', dns_name: 'Hostname', description: 'Description', comments: 'Management marker' };
                    var display = function (value) { return value == null ? 'Not set' : typeof value === 'string' && value.indexOf('DDI:') === 0 ? 'Managed by DDI' : productLabel(String(value)); };
                    if (JSON.stringify(before) !== JSON.stringify(fields[name])) diff.append($('<li>').text((labels[name] || name) + ': ' + display(before) + ' → ' + display(fields[name])));
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
