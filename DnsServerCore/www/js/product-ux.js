/* Shared presentation and accessibility; service identifiers and values remain unchanged. */
var productLabels = Object.freeze({
    FalsePositive: 'False positive', DenyNewLeases: 'Withhold new leases',
    DenyNewLease: 'New lease withheld',
    SecondaryForwarder: 'Secondary forwarder', SecondaryCatalog: 'Secondary catalog',
    DomainSuffix: 'Domain and subdomains', Domain: 'Exact domain', Network: 'IP address or network',
    DhcpLease: 'DHCP lease', DhcpReservation: 'DHCP reservation', DnsRecord: 'DNS record',
    DhcpScope: 'DHCP scope', DnsReverseZone: 'Reverse DNS zone', DhcpGateway: 'DHCP gateway',
    DhcpPool: 'Dynamic pool', DhcpExclusion: 'Excluded range', Gateway: 'Gateway', 'discovery.sync': 'Inventory refreshed',
    Investigating: 'Investigating', Confirmed: 'Confirmed', Resolved: 'Resolved',
    UnmanagedObservedAddress: 'Unassigned observed address', MissingDnsRecord: 'Missing DNS record',
    'subnet.set': 'Network saved', 'view.set': 'Network view saved', 'assignment.set': 'Assignment saved',
    'subnet.delete': 'Network deleted', 'view.delete': 'Network view deleted', 'assignment.delete': 'Assignment deleted',
    Unmanaged: 'Unassigned address', HostNameDrift: 'Hostname mismatch', DuplicateLease: 'Conflicting leases', Unobserved: 'No recent observations'
});
function productLabel(value) { return productLabels[value] || value || '—'; }
function productTime(value) { var date = new Date(value); return value && !isNaN(date.getTime()) ? date.toLocaleString(undefined, {timeZoneName: 'short'}) : '—'; }
function productCount(count, singular, plural) { return count + ' ' + (count === 1 ? singular : plural || singular + 's'); }
function productReason(value) { return String(value || '').replace(/^(?:Shared device security signal:\s*)+/, 'Shared device risk: '); }
function productEmptyRow(body, columns, message) {
    if (!body.children().length) body.append($('<tr>').append($('<td>').attr('colspan', columns).addClass('text-muted').text(message)));
}
function productDetails(target, fields) {
    var list = $('<dl>').addClass('ux-details');
    fields.forEach(function (field) { list.append($('<dt>').text(field[0])).append($('<dd>').text(field[1] == null || field[1] === '' ? '—' : field[1])); });
    target.append(list);
}
function productDnsValue(record) {
    var data = record.rData || record.RDATA || {};
    if (typeof data === 'string') return data;
    var type = record.type || record.Type;
    var value = function (lower, upper) { return data[lower] == null ? data[upper] : data[lower]; };
    switch (type) {
        case 'A': case 'AAAA': return value('ipAddress', 'IPAddress');
        case 'NS': return value('nameServer', 'NameServer');
        case 'CNAME': return value('cname', 'Domain') || data.CNAME;
        case 'DNAME': return value('dname', 'Domain') || data.DNAME;
        case 'PTR': return value('ptrName', 'Domain') || data.PTR;
        case 'TXT': return value('text', 'Text');
        case 'MX': return value('preference', 'Preference') + ' · ' + value('exchange', 'Exchange');
        case 'SRV': return 'Priority ' + value('priority', 'Priority') + ' · Weight ' + value('weight', 'Weight') + ' · Port ' + value('port', 'Port') + ' · ' + value('target', 'Target');
        case 'SOA': return 'Primary server: ' + value('primaryNameServer', 'PrimaryNameServer') + ' · Serial: ' + value('serial', 'Serial') + ' · Refresh: ' + value('refresh', 'Refresh') + ' · Retry: ' + value('retry', 'Retry') + ' · Expiry: ' + value('expire', 'Expire');
        case 'OPT': return (data.Options || []).map(function (option) { var info = option.Data || {}; return info.ExtraText || info.InfoCode || option.Code; }).join(' · ') || 'EDNS options';
        default: return data.data || data.Data || 'Additional record fields are available in Developer data.';
    }
}
function productDnsRecords(target, records) {
    var table = $('<table>').addClass('table table-striped'), body = $('<tbody>');
    table.append($('<thead>').append($('<tr>').append(['Name', 'Type', 'TTL', 'Data'].map(function (name) { return $('<th>').text(name); })))).append(body);
    (records || []).forEach(function (record) { var row = $('<tr>'); [record.nameIdn || record.NameIDN || record.name || record.Name || '.', record.type || record.Type, record.ttl || record.TTL || 0, productDnsValue(record)].forEach(function (value) { row.append($('<td>').text(value == null ? '—' : value)); }); body.append(row); });
    productEmptyRow(body, 4, 'No records in this response.'); target.append($('<div>').addClass('table-responsive').append(table));
}
function productDnsView(target, data) {
    target.empty();
    if (Array.isArray(data)) productDnsRecords(target, data);
    else {
        productDetails(target, [['Status', data.RCODE], ['Authoritative answer', data.AuthoritativeAnswer ? 'Yes' : 'No'], ['DNSSEC authenticated', data.AuthenticData ? 'Yes' : 'No']]);
        if (data.Metadata) productDetails(target, [['Responding server', data.Metadata.NameServer], ['Transport', data.Metadata.Protocol], ['Response time', data.Metadata.RoundTripTime]]);
        if (data.DnsClientExtendedErrors && data.DnsClientExtendedErrors.length) target.append($('<p>').text(data.DnsClientExtendedErrors.map(function (error) { return error.ExtraText || error.InfoCode; }).join(' · ')));
        [['Answers', data.Answer], ['Authority', data.Authority], ['Additional records', data.Additional]].forEach(function (section) { if (section[1] && section[1].length) { target.append($('<h4>').text(section[0])); productDnsRecords(target, section[1]); } });
        if (!data.Answer || !data.Answer.length) target.append($('<p>').text('No answer records returned.'));
    }
    target.append($('<details>').append($('<summary>').text('Developer data')).append($('<pre>').text(JSON.stringify(data, null, 2))));
}
(function () {
    var nextId = 0;
    function enhance(root) {
        var surface = $(root);
        surface.find('table.table').addBack('table.table').each(function () {
            if (!$(this).closest('.table-responsive').length) $(this).wrap('<div class="table-responsive ux-table"></div>');
            $(this).find('thead th').attr('scope', 'col');
        });
        surface.find('.ux-advanced').addBack('.ux-advanced').each(function () {
            if (!$(this).parent().is('details')) $(this).wrap('<details class="ux-disclosure"></details>').before($('<summary>').text($(this).attr('data-ux-title')));
        });
        surface.find('input, select, textarea').addBack('input, select, textarea').each(function () {
            var control = $(this), type = control.attr('type');
            if (type === 'hidden' || type === 'button' || type === 'submit' || control.attr('aria-label') || control.attr('aria-labelledby') || control.closest('label').length) return;
            var id = control.attr('id');
            if (id && $('label').filter(function () { return this.htmlFor === id; }).length) return;
            var cell = control.closest('td,th'), header = cell.length ? cell.closest('table').find('thead th').eq(cell.index()).text().trim() : '';
            if (cell.length) {
                var context = control.closest('tr').find('td').filter(function () { return $(this).text().trim() && !/^\d+$/.test($(this).text().trim()) && !$(this).find('button,input,select,textarea').length; }).first().text().trim();
                control.attr('aria-label', header || (type === 'checkbox' ? (cell.is('th') ? 'Select all rows' : 'Select ' + (context || 'row')) : control.attr('placeholder') || 'Value'));
                return;
            }
            var label = control.closest('.form-group').find('label.control-label').first();
            if (label.length) {
                if (!id) { id = 'ux-control-' + (++nextId); control.attr('id', id); }
                control.attr('aria-label', label.text().trim());
            } else {
                var name = control.attr('placeholder') || control.attr('title');
                if (name) control.attr('aria-label', name);
            }
        });
        surface.find('button.close').attr('aria-label', 'Close');
        surface.find('canvas').each(function () { var canvas = $(this); if (!canvas.attr('aria-label')) canvas.attr({role: 'img', 'aria-label': canvas.attr('id') === 'canvasDashboardMain' ? 'DNS queries over time' : 'DNS query distribution'}); });
        surface.find('button, a, [role=button]').addBack('button, a, [role=button]').each(function () {
            var button = $(this); if (button.attr('aria-label') || button.text().trim()) return;
            var name = button.attr('title') || button.attr('data-original-title');
            if (!name) {
                var icons = { 'glyphicon-trash': 'Delete', 'glyphicon-pencil': 'Edit', 'glyphicon-remove': 'Remove', 'glyphicon-refresh': 'Refresh', 'fa-trash': 'Delete', 'fa-pencil': 'Edit', 'fa-times': 'Remove' };
                Object.keys(icons).some(function (icon) { if (button.find('.' + icon).length) { name = icons[icon]; return true; } return false; });
            }
            if (!name && button.attr('data-toggle') === 'dropdown' && button.closest('tr').length) name = 'More actions';
            if (!name && button.is('span[role=button]') && button.attr('data-toggle') === 'dropdown') name = 'Choose server';
            if (name) button.attr('aria-label', name);
            if (button.is('span[role=button]')) button.attr('tabindex', '0');
        });
        surface.find('.modal').each(function () {
            var dialog = $(this), title = dialog.find('.modal-title').first();
            dialog.attr({ 'aria-modal': 'true', role: 'dialog' });
            if (title.length && !dialog.attr('aria-labelledby')) {
                if (!title.attr('id')) title.attr('id', 'ux-dialog-title-' + (++nextId));
                dialog.attr('aria-labelledby', title.attr('id'));
            }
        });
        surface.find('option').each(function () {
            var option = $(this), text = option.text();
            if (productLabels[text]) { if (!option.attr('value')) option.attr('value', text); option.text(productLabels[text]); }
        });
    }
    $(function () {
        enhance(document.body);
        var pending = new Set(), frame = null;
        new MutationObserver(function (changes) {
            changes.forEach(function (change) { change.addedNodes.forEach(function (node) { if (node.nodeType === 1) pending.add(node); }); });
            if (pending.size && frame === null) frame = requestAnimationFrame(function () { var roots = Array.from(pending); pending.clear(); frame = null; roots.forEach(enhance); });
        }).observe(document.body, { childList: true, subtree: true });
        $(document).on('show.bs.modal', '.modal', function () { $(this).data('ux-return-focus', document.activeElement); })
        .on('hidden.bs.modal', '.modal', function () {
            var source = $(this).data('ux-return-focus'); if (source && source.isConnected && $(source).is(':visible') && !source.disabled) $(source).trigger('focus');
        }).on('shown.bs.modal', '.modal', function () {
            if (!$(this).find(':focus').length) $(this).find('input:not([type=hidden]):visible:enabled, select:visible:enabled, textarea:visible:enabled, button:visible:enabled').first().trigger('focus');
        });
        // Existing validation code requests focus with jQuery. Reveal a collapsed section first,
        // so a field-level repair remains possible when secondary forms use disclosure.
        var originalTrigger = $.fn.trigger;
        $.fn.trigger = function (event) {
            if (event === 'focus') this.parents('details').prop('open', true);
            return originalTrigger.apply(this, arguments);
        };
        document.addEventListener('invalid', function (event) { $(event.target).parents('details').prop('open', true); }, true);
        $(document).on('keydown', 'span[role=button]', function (event) { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); $(this).trigger('click'); } });
        function placeMenu(dropdown) {
            var toggle = dropdown.find('[data-toggle=dropdown]').first(), menu = dropdown.children('.dropdown-menu');
            if (!toggle.length || !menu.length) return;
            var bounds = toggle[0].getBoundingClientRect(), width = Math.min(menu.outerWidth(), window.innerWidth - 16);
            var below = window.innerHeight - bounds.bottom - 12, above = bounds.top - 12, upward = below < 160 && above > below;
            var height = Math.min(menu[0].scrollHeight, Math.max(80, upward ? above : below));
            menu.css({ position: 'fixed', top: upward ? Math.max(8, bounds.top - height) : bounds.bottom, left: Math.max(8, Math.min(bounds.right - width, window.innerWidth - width - 8)), right: 'auto', width: width, maxHeight: height, overflowY: 'auto', zIndex: 1100 });
        }
        $(document).on('shown.bs.dropdown', '.dropdown', function () {
            var dropdown = $(this); if (!dropdown.closest('.table-responsive').length) return;
            var menu = dropdown.children('.dropdown-menu'); menu.data('ux-original-style', menu.attr('style') || ''); placeMenu(dropdown);
        }).on('hidden.bs.dropdown', '.dropdown', function () {
            var menu = $(this).children('.dropdown-menu'); if (menu.data('ux-original-style') !== undefined) { menu.attr('style', menu.data('ux-original-style')); menu.removeData('ux-original-style'); }
        });
        window.addEventListener('resize', function () { $('.table-responsive .dropdown.open').each(function () { placeMenu($(this)); }); });
        document.addEventListener('scroll', function () { $('.table-responsive .dropdown.open').each(function () { placeMenu($(this)); }); }, true);
    });
})();
