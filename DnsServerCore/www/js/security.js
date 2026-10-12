var ddiSecurityPolicy = null;

function securityRequest(path, method, data, success) {
    HTTPRequest({ url: '/api/security/' + path, method: method, data: data || {}, token: sessionData.token,
        success: success, invalidToken: showPageLogin });
}

function refreshSecurity() {
    securityRequest('policy/get', 'GET', {}, function (response) {
        var data = response.response;
        ddiSecurityPolicy = data.policy;
        var p = ddiSecurityPolicy;
        $('.security-edit').toggle(sessionData.info.permissions.Settings.canModify);
        $('#securityRevision').text('Policy revision ' + p.revision + ' · ' + (p.enabled ? p.mode : 'Disabled'));
        $('#securityEnabled').prop('checked', p.enabled); $('#securityMode').val(p.mode);
        $('#securityAuthoritative').prop('checked', p.inspectAuthoritative); $('#securityAlertThreshold').val(p.alertThreshold);
        $('#securityDeviceThreshold').val(p.deviceThreshold); $('#securityDeviceDns').val(p.deviceDnsResponse); $('#securityDhcpResponse').val(p.dhcpResponse);
        $('#securityMispEnabled').prop('checked', p.misp.enabled); $('#securityMispUrl').val(p.misp.url);
        $('#securityMispCredential').val(p.misp.apiKeyEnvironment); $('#securityMispRefresh').val(p.misp.refreshSeconds); $('#securityMispLifetime').val(p.misp.cacheLifetimeHours);
        $('#securityMispEnabled, #securityMispUrl, #securityMispCredential, #securityMispRefresh, #securityMispLifetime').prop('disabled', !sessionData.info.permissions.Administration.canModify);
        $('#securityHealth').text('MISP: ' + data.mispHealth.status + ' (' + data.mispHealth.indicators + ' cached indicators). Signal distribution: ' + data.signalHealth.status + '. Dropped event deliveries: ' + data.droppedEvents + '.');
        var body = $('#securityIndicatorsBody').empty();
        p.indicators.forEach(function (indicator) {
            var row = $('<tr>');
            [indicator.kind, indicator.value, indicator.action, indicator.source, indicator.confidence, indicator.clientNetwork || 'All clients', indicator.expires || 'No expiry'].forEach(function (v) { row.append($('<td>').text(v)); });
            var actions = $('<td>');
            if (sessionData.info.permissions.Settings.canModify) actions.append($('<button class="btn btn-xs btn-default">').text('Remove').on('click', function () {
                var next = Object.assign({}, ddiSecurityPolicy, { indicators: ddiSecurityPolicy.indicators.filter(function (i) { return i.id !== indicator.id; }) });
                submitSecurityPolicy(next);
            }));
            row.append(actions); body.append(row);
        });
        refreshSecurityEvents();
    });
}

function submitSecurityPolicy(policy) {
    securityRequest('policy/set', 'POST', { revision: ddiSecurityPolicy.revision, policy: JSON.stringify(policy) }, function () {
        refreshSecurity(); showAlert('success', 'Saved', 'Security policy updated.');
    });
}

function saveSecuritySettings() {
    if (!ddiSecurityPolicy) return;
    var policy = Object.assign({}, ddiSecurityPolicy, {
        enabled: $('#securityEnabled').prop('checked'), mode: $('#securityMode').val(), inspectAuthoritative: $('#securityAuthoritative').prop('checked'),
        alertThreshold: Number($('#securityAlertThreshold').val()), deviceThreshold: Number($('#securityDeviceThreshold').val()),
        deviceDnsResponse: $('#securityDeviceDns').val(), dhcpResponse: $('#securityDhcpResponse').val(),
        misp: { enabled: $('#securityMispEnabled').prop('checked'), url: $('#securityMispUrl').val(), apiKeyEnvironment: $('#securityMispCredential').val(), refreshSeconds: Number($('#securityMispRefresh').val()), cacheLifetimeHours: Number($('#securityMispLifetime').val()) }
    });
    submitSecurityPolicy(policy);
}

function addSecurityIndicators() {
    if (!ddiSecurityPolicy) return;
    var values = $('#securityIndicatorValues').val().split(/\r?\n/).map(function (v) { return v.trim(); }).filter(function (v) { return v && v[0] !== '#'; });
    var expiry = $('#securityIndicatorExpiry').val();
    var additions = values.map(function (v) {
        return { id: crypto.randomUUID(), kind: $('#securityIndicatorKind').val(), value: v, action: $('#securityIndicatorAction').val(), source: $('#securityIndicatorSource').val(),
            confidence: Number($('#securityIndicatorConfidence').val()), expires: expiry ? new Date(expiry).toISOString() : null, clientNetwork: $('#securityIndicatorClient').val() || null, evidence: null };
    });
    submitSecurityPolicy(Object.assign({}, ddiSecurityPolicy, { indicators: ddiSecurityPolicy.indicators.concat(additions) }));
}

function refreshSecurityEvents() {
    securityRequest('events/list', 'GET', {}, function (response) {
        var events = $('#securityEventsBody').empty();
        response.response.events.slice().reverse().forEach(function (event) {
            var row = $('<tr>');
            [event.timestamp, event.node, event.service, event.clientAddress || (event.device ? event.device.hardwareAddress : ''), event.query, event.action,
                event.behavior ? event.behavior.score + ' / 100' : '', event.device ? event.device.scope + ' / ' + (event.device.hostName || 'Unknown hostname') : 'Attribution unavailable', event.reason].forEach(function (v) { row.append($('<td>').text(v)); });
            var evidence = $('<td>');
            evidence.append($('<button class="btn btn-xs btn-default">').text('Evidence').on('click', function () { $('#securityEvidence').text(JSON.stringify(event, null, 2)); }));
            row.append(evidence); events.append(row);
        });
        var signals = $('#securitySignalsBody').empty();
        response.response.signals.forEach(function (signal) {
            var row = $('<tr>');
            [signal.node, signal.service, signal.scope, signal.hardwareAddress, signal.score, signal.expires, signal.reason].forEach(function (v) { row.append($('<td>').text(v)); });
            signals.append(row);
        });
    });
}

function simulateSecurity() {
    securityRequest('simulate', 'GET', { query: $('#securitySimQuery').val(), client: $('#securitySimClient').val(), cnames: $('#securitySimCnames').val(), addresses: $('#securitySimAddresses').val() }, function (response) {
        var decision = response.response.decision;
        var output = $('#securitySimulation').empty();
        output.append($('<h4>').text(decision.action)).append($('<p>').text(decision.reason));
        var evidence = $('<ul>');
        decision.matches.forEach(function (match) { evidence.append($('<li>').text(match.kind + ': ' + match.value + ' — source ' + match.source + ' — confidence ' + match.confidence)); });
        output.append(evidence);
        if (decision.additionalMatches) output.append($('<p>').text(decision.additionalMatches + ' additional matches omitted from the bounded evidence sample.'));
    });
}
var securityTimelineResult = null;
function securitySearchTimeline() {
    var now = new Date(), from = $('#securitySearchFrom').val() || new Date(now.getTime() - 3600000).toISOString(), to = $('#securitySearchTo').val() || now.toISOString();
    securityRequest('events/search', 'GET', { from: from, to: to, client: $('#securitySearchClient').val(), query: $('#securitySearchQuery').val(), limit: 200 }, function (response) {
        securityTimelineResult = response.response.search; var output = $('#securityTimeline').empty(), list = $('<ul>');
        output.append($('<p>').text(securityTimelineResult.events.length + ' events; ' + securityTimelineResult.scanned + ' persisted lines scanned; ' + securityTimelineResult.corruptLines + ' unreadable lines; ' + (securityTimelineResult.truncated ? 'partial coverage — narrow the search interval.' : 'search bounds were not reached.')));
        securityTimelineResult.events.forEach(function (e) { list.append($('<li>').text(e.timestamp + ' — ' + e.service + ' — ' + e.action + ' — ' + (e.query || e.clientAddress || '') + ' — ID ' + e.id + ' — ' + e.reason)); }); output.append(list);
        securityLoadDispositions();
    });
}
function securityExportTimeline() {
    if (!securityTimelineResult) return;
    var url = URL.createObjectURL(new Blob([JSON.stringify(securityTimelineResult, null, 2)], { type: 'application/json' }));
    var link = document.createElement('a'); link.href = url; link.download = 'ddi-security-search.json'; link.click(); URL.revokeObjectURL(url);
}
function securityLoadDispositions() {
    securityRequest('dispositions/list', 'GET', {}, function (response) { var list = $('#securityDispositions').empty(); response.response.dispositions.forEach(function (d) { list.append($('<li>').text(d.eventId + ' — ' + d.status + ' — ' + d.actor + ' — ' + d.notes)); }); });
}
function securitySetDisposition() {
    securityRequest('dispositions/set', 'POST', { eventId: $('#securityDispositionId').val(), status: $('#securityDispositionStatus').val(), notes: $('#securityDispositionNotes').val() }, securityLoadDispositions);
}
function securityLoadHistory() {
    securityRequest('policy/history', 'GET', {}, function (response) { var list = $('#securityPolicyHistory').empty(); response.response.policies.forEach(function (p) { list.append($('<li>').text('Revision ' + p.revision + ': ' + (p.enabled ? 'enabled' : 'disabled') + ', ' + p.mode + ', ' + p.indicators + ' local indicators')); }); });
}
function securityRollback() {
    if (!ddiSecurityPolicy) return;
    securityRequest('policy/rollback', 'POST', { revision: ddiSecurityPolicy.revision, targetRevision: $('#securityRollbackRevision').val() }, function () { refreshSecurity(); securityLoadHistory(); });
}
