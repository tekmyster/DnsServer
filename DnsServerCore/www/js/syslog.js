var syslogSavedSettings = null;
var syslogBusy = false;
function syslogCanModify() {
    return !!(sessionData && sessionData.info.permissions.Settings.canModify && sessionData.info.permissions.Administration.canModify);
}
function syslogLocalNode() {
    var node = $('#optSettingsClusterNode').val() || '';
    return node === '' || node === sessionData.info.dnsServerDomain;
}
function syslogControls() {
    var enabled = !syslogBusy && syslogSavedSettings && syslogCanModify() && syslogLocalNode();
    $('#syslogFields').prop('disabled', !enabled);
    $('#syslogSave').prop('disabled', !enabled);
    $('#syslogTest').prop('disabled', !enabled || !syslogSavedSettings.enabled || $('#syslogPanel').data('dirty') === true);
    $('#syslogFraming').prop('disabled', !enabled || $('#syslogTransport').val() !== 'Tcp');
}
function renderSyslog(data) {
    syslogSavedSettings = data.settings;
    var s = data.settings;
    $('#syslogEnabled').prop('checked', s.enabled); $('#syslogServer').val(s.server); $('#syslogPort').val(s.port);
    $('#syslogTransport').val(s.transport); $('#syslogFacility').val(String(s.facility)); $('#syslogFraming').val(s.tcpFraming);
    $('#syslogSystem').prop('checked', s.systemEvents); $('#syslogSecurity').prop('checked', s.securityEvents); $('#syslogQueries').prop('checked', s.dnsQueries);
    $('#syslogPanel').data('dirty', false);
    var h = data.health;
    var states = { Disabled: 'Disabled', Waiting: 'Waiting for events', Sending: 'Forwarding', Error: 'Send failed' };
    var pieces = [states[h.state] || 'Unavailable', 'Sent: ' + h.sent.toLocaleString(), 'Queued: ' + h.queued.toLocaleString(), 'Dropped: ' + h.dropped.toLocaleString(), 'Failed writes: ' + h.failed.toLocaleString()];
    if (h.lastSentUtc) pieces.push('Last sent: ' + productTime(h.lastSentUtc));
    if (h.lastError) pieces.push(h.lastError);
    $('#syslogHealth').text(pieces.join(' · '));
    syslogControls();
}
function refreshSyslogSettings(keepMessage) {
    if (!syslogLocalNode()) { syslogSavedSettings = null; $('#syslogFeedback').text('Open that server’s console to configure its syslog forwarding.'); $('#syslogHealth').text(''); syslogControls(); return; }
    syslogBusy = true; syslogControls();
    if (!keepMessage) $('#syslogFeedback').text('Loading forwarding settings…');
    HTTPRequest({url:'api/logging/syslog/get', method:'GET', token:sessionData.token, dontHideAlert:true,
        success:function(r) { syslogBusy=false; renderSyslog(r.response); if (!keepMessage) $('#syslogFeedback').text(''); },
        error:function() {syslogBusy=false; syslogSavedSettings=null; $('#syslogFeedback').text('Could not load forwarding settings. Choose Refresh to try again.'); syslogControls();},
        invalidToken:function(){syslogBusy=false; syslogSavedSettings=null; syslogControls(); showPageLogin();}
    });
}
function saveSyslogSettings() {
    if (syslogBusy || !syslogSavedSettings || !syslogCanModify()) return;
    var settings={enabled:$('#syslogEnabled').prop('checked'),server:$('#syslogServer').val().trim(),port:Number($('#syslogPort').val()),transport:$('#syslogTransport').val(),facility:Number($('#syslogFacility').val()),tcpFraming:$('#syslogFraming').val(),systemEvents:$('#syslogSystem').prop('checked'),securityEvents:$('#syslogSecurity').prop('checked'),dnsQueries:$('#syslogQueries').prop('checked')};
    if (settings.enabled && !settings.server) {$('#syslogFeedback').text('Enter the syslog server address.'); $('#syslogServer').trigger('focus'); return;}
    if (!Number.isInteger(settings.port) || settings.port<1 || settings.port>65535) {$('#syslogFeedback').text('Enter a port between 1 and 65535.'); $('#syslogPort').trigger('focus'); return;}
    syslogBusy=true; syslogControls(); $('#syslogFeedback').text('Saving forwarding settings…');
    HTTPRequest({url:'api/logging/syslog/set',method:'POST',token:sessionData.token,data:{settings:JSON.stringify(settings),revision:syslogSavedSettings.revision},
        success:function(r){syslogBusy=false; renderSyslog(r.response); $('#syslogFeedback').text('Forwarding settings saved.');},
        error:function(){syslogBusy=false; syslogControls(); $('#syslogFeedback').text('Forwarding settings were not saved. Check the error and try again.');},
        invalidToken:function(){syslogBusy=false;syslogControls();showPageLogin();}
    });
}
function sendSyslogTest() {
    if (syslogBusy || !syslogSavedSettings || !syslogSavedSettings.enabled || !syslogCanModify()) return;
    syslogBusy=true; syslogControls();
    HTTPRequest({url:'api/logging/syslog/test',method:'POST',token:sessionData.token,
        success:function(r){syslogBusy=false;renderSyslog(r.response);$('#syslogFeedback').text('Test queued. Confirm receipt on your syslog server.');},
        error:function(){syslogBusy=false;syslogControls();$('#syslogFeedback').text('The test was not queued. Check forwarding status and try again.');},
        invalidToken:function(){syslogBusy=false;syslogControls();showPageLogin();}
    });
}
$(function(){
    var facilities=['Kernel','User','Mail','Daemon','Security','Syslog','Printer','News','UUCP','Clock','Private security','FTP','NTP','Audit','Alert','Clock 2','Local 0','Local 1','Local 2','Local 3','Local 4','Local 5','Local 6','Local 7'];
    facilities.forEach(function(name,i){$('#syslogFacility').append($('<option>').val(String(i)).text(name));});
    $('#syslogFields').on('input change',function(){$('#syslogPanel').data('dirty',true);syslogControls();});
});
