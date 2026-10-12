var ipamInventory = null;

function ipamRequest(path, method, data, success) {
    HTTPRequest({ url: "/api/ipam/" + path, method: method, data: data || {}, token: sessionData.token,
        success: success, invalidToken: showPageLogin });
}

function ipamOptions(selector, entries, label) {
    var previous = $(selector).val();
    var select = $(selector).empty();
    entries.forEach(function (entry) { select.append($('<option>').val(entry.id).text(label(entry))); });
    if (entries.some(function (entry) { return entry.id === previous; })) select.val(previous);
}

function refreshIpam() {
    ipamRequest("list", "GET", {}, function (response) {
        ipamInventory = response.response.inventory;
        var permissions = sessionData.info.permissions.Settings;
        $("#ddiNetBoxPanel").toggle(sessionData.info.permissions.Administration.canModify && permissions.canModify);
        $(".ipam-edit").toggle(permissions.canModify);
        $("#ipamRevision").text("Inventory revision " + ipamInventory.revision);
        ipamOptions("#ipamView", ipamInventory.views, function (v) { return v.name; });
        ipamOptions("#ipamSubnet", ipamInventory.subnets.filter(function (s) { return !s.container; }), function (s) { return s.name + " — " + s.prefix; });
        ipamOptions("#ipamInspectSubnet", ipamInventory.subnets.filter(function (s) { return !s.container; }), function (s) {
            var view = ipamInventory.views.find(function (v) { return v.id === s.viewId; });
            return (view ? view.name : s.viewId) + " / " + s.name + " / " + s.prefix;
        });
        var table = $("#ipamSubnetsBody").empty();
        ipamInventory.subnets.forEach(function (s) {
            var row = $('<tr>');
            var view = ipamInventory.views.find(function (v) { return v.id === s.viewId; });
            [view ? view.name : s.viewId, s.name, s.prefix, s.container ? "Container" : "Subnet", s.site, s.vlan || "", s.dhcpScope || "", s.dnsZone || ""].forEach(function (value) { row.append($('<td>').text(value)); });
            var actions = $('<td>');
            if (permissions.canModify) actions.append($('<button class="btn btn-xs btn-default">').text("Edit").on('click', function () {
                $('#ipamSubnetId').val(s.id); $('#ipamView').val(s.viewId); $('#ipamPrefix').val(s.prefix); $('#ipamSubnetName').val(s.name);
                $('#ipamSite').val(s.site); $('#ipamVlan').val(s.vlan || ''); $('#ipamDhcpScope').val(s.dhcpScope); $('#ipamDnsZone').val(s.dnsZone); $('#ipamContainer').prop('checked', s.container);
            }));
            if (permissions.canDelete) actions.append($('<button class="btn btn-xs btn-danger">').text("Delete").on('click', function () { ipamDelete('subnet', s.id); }));
            row.append(actions); table.append(row);
        });
        var assignments = $("#ipamAssignmentsBody").empty();
        ipamInventory.assignments.forEach(function (a) {
            var row = $('<tr>');
            var subnet = ipamInventory.subnets.find(function (s) { return s.id === a.subnetId; });
            [subnet ? subnet.name : a.subnetId, a.address, a.hostName, a.owner, a.status, a.description].forEach(function (value) { row.append($('<td>').text(value)); });
            var actions = $('<td>');
            if (permissions.canModify) actions.append($('<button class="btn btn-xs btn-default">').text("Edit").on('click', function () {
                $('#ipamAssignmentId').val(a.id); $('#ipamSubnet').val(a.subnetId); $('#ipamAddress').val(a.address); $('#ipamHostName').val(a.hostName);
                $('#ipamOwner').val(a.owner); $('#ipamStatus').val(a.status); $('#ipamDescription').val(a.description);
            }));
            if (permissions.canDelete) actions.append($('<button class="btn btn-xs btn-danger">').text("Delete").on('click', function () { ipamDelete('assignment', a.id); }));
            row.append(actions); assignments.append(row);
        });
        var history = $('#ipamHistory').empty();
        ipamInventory.changes.slice(-20).reverse().forEach(function (c) { history.append($('<li>').text(c.timestamp + ' — ' + c.actor + ' — ' + c.operation + ' — revision ' + c.revision)); });
        $('#ipamInspection').empty();
    });
}

function ipamSave(kind) {
    if (!ipamInventory) return;
    var data = { revision: ipamInventory.revision };
    if (kind === 'view') {
        data.name = $('#ipamViewName').val(); data.description = $('#ipamViewDescription').val();
    } else if (kind === 'subnet') {
        data.id = $('#ipamSubnetId').val(); data.viewId = $('#ipamView').val(); data.prefix = $('#ipamPrefix').val(); data.name = $('#ipamSubnetName').val();
        data.site = $('#ipamSite').val(); data.vlan = $('#ipamVlan').val(); data.dhcpScope = $('#ipamDhcpScope').val(); data.dnsZone = $('#ipamDnsZone').val(); data.container = $('#ipamContainer').prop('checked');
    } else {
        data.id = $('#ipamAssignmentId').val(); data.subnetId = $('#ipamSubnet').val(); data.address = $('#ipamAddress').val(); data.hostName = $('#ipamHostName').val();
        data.owner = $('#ipamOwner').val(); data.status = $('#ipamStatus').val(); data.description = $('#ipamDescription').val();
    }
    ipamRequest(kind === 'view' ? 'views/set' : kind === 'subnet' ? 'subnets/set' : 'assignments/set', 'POST', data, function () {
        $('#ipamSubnetId, #ipamAssignmentId').val(''); refreshIpam(); showAlert('success', 'Saved', 'IPAM inventory updated.');
    });
}

function ipamDelete(kind, id) {
    if (!confirm('Delete this IPAM ' + kind + '?')) return;
    ipamRequest('delete', 'POST', { kind: kind, id: id, revision: ipamInventory.revision }, refreshIpam);
}

function ipamNextAvailable() {
    var subnet = $('#ipamSubnet').val();
    if (!subnet) return;
    ipamRequest('addresses/nextAvailable', 'GET', { subnetId: subnet }, function (response) { $('#ipamAddress').val(response.response.address); });
}

function inspectIpam() {
    var subnet = $('#ipamInspectSubnet').val();
    if (!subnet) return;
    ipamRequest('subnets/inspect', 'GET', { subnetId: subnet }, function (response) {
        var data = response.response;
        var output = $('#ipamInspection').empty();
        output.append($('<p>').text(data.utilization.knownUsedAddresses + ' known used / ' + data.utilization.usableAddresses + ' usable addresses. ' + data.utilization.intendedAddresses + ' intended; ' + data.utilization.observedAddresses + ' observed.'));
        var findings = $('<ul>');
        data.findings.forEach(function (f) { findings.append($('<li>').text(f.address + ' — ' + f.kind + ': ' + f.detail)); });
        if (!data.findings.length) findings.append($('<li>').text('No reconciliation findings.'));
        output.append(findings);
        var threats = $('<ul>');
        (data.securityFindings || []).forEach(function (f) { threats.append($('<li>').text(f.address + ' — ' + f.action + ' — ' + f.source + ' — ' + f.reason + ' Indicators: ' + f.indicators.join(', '))); });
        if ((data.securityFindings || []).length) output.append($('<h4>').text('Threat intelligence matches')).append(threats);
        var observations = $('<ul>');
        data.observations.forEach(function (o) { observations.append($('<li>').text(o.address + ' — ' + o.source + ' — ' + (o.hostName || '') + ' — ' + (o.clientIdentifier || ''))); });
        output.append($('<h4>').text('DNS and DHCP observations')).append(observations);
    });
}
