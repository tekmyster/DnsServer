/* Deployment portability keeps identity and allocation authority local. */
var ddiMigrationReviewToken = null;
var ddiMigrationGeneration = 0;
function resetDdiMigrationReview() {
    ddiMigrationGeneration++; ddiMigrationReviewToken = null;
    $("#ddiMigrationReview").prop("hidden", true); $("#ddiMigrationImport").prop("disabled", true);
    $("#ddiMigrationAlert").empty();
}
function openDdiMigration() {
    resetDdiMigrationReview(); $("#ddiMigrationFile").val(""); $("#modalDdiMigration").one("shown.bs.modal", function () { $("#ddiMigrationFile").trigger("focus"); }).modal("show");
}
async function downloadDdiArchive(url, button, placeholder, filename) {
    button.button("loading");
    try {
        var response = await fetch(url, {headers: {Authorization: "Bearer " + sessionData.token}, cache: "no-store"});
        if (!response.ok || !(response.headers.get("content-type") || "").includes("application/zip")) {
            var result = await response.json(); throw new Error(result.errorMessage || "The export failed. Retry the download.");
        }
        var blob = await response.blob(); var link = document.createElement("a");
        var address = URL.createObjectURL(blob); link.href = address; link.download = filename;
        document.body.appendChild(link); link.click(); link.remove(); setTimeout(function () { URL.revokeObjectURL(address); }, 30000);
        return true;
    } catch (error) { showAlert("danger", "Download failed", error.message, placeholder); return false; }
    finally { button.button("reset"); }
}
async function exportDdiMigration() {
    if (await downloadDdiArchive("api/settings/migration/export", $("#ddiMigrationExport"), $("#ddiMigrationAlert"), "ddi-new-deployment.zip"))
        showAlert("success", "Package ready", "The archive is ready to save. Store it securely; it contains DNS keys and lease data.", $("#ddiMigrationAlert"));
}
async function previewDdiMigration() {
    resetDdiMigrationReview(); var generation = ddiMigrationGeneration;
    var file = $("#ddiMigrationFile")[0].files[0];
    if (!file || file.size > 16 * 1024 * 1024) { showAlert("warning", "Select a package", "Choose a ZIP file up to 16 MiB.", $("#ddiMigrationAlert")); $("#ddiMigrationFile").trigger("focus"); return; }
    var data = new FormData(); data.append("file", file); var button = $("#ddiMigrationPreview"); button.button("loading");
    try {
        var response = await fetch("api/settings/migration/preview", {method: "POST", headers: {Authorization: "Bearer " + sessionData.token}, body: data});
        var result = await response.json(); if (generation !== ddiMigrationGeneration) return;
        if (result.status !== "ok") throw new Error(result.errorMessage || "The package could not be checked.");
        var view = result.response;
        $("#ddiMigrationZones").text(view.zones); $("#ddiMigrationScopes").text(view.scopes);
        $("#ddiMigrationDns").text(view.dnsSettings ? "Included" : "Retain destination settings");
        $("#ddiMigrationIpam").text(view.inventory ? "Included" : "Not included");
        $("#ddiMigrationSecurity").text(view.security ? "Included" : "Not included");
        $("#ddiMigrationIntegrity").text(view.verifiedManifest ? "File checksums verified" : "Legacy backup; no checksum manifest");
        $("#ddiMigrationReview").prop("hidden", false); $("#ddiMigrationImport").prop("disabled", !view.canImport);
        ddiMigrationReviewToken = view.canImport ? view.review : null;
        if (!view.canImport) showAlert("warning", "Destination unavailable", view.reason, $("#ddiMigrationAlert"));
    } catch (error) { if (generation === ddiMigrationGeneration) showAlert("danger", "Preview failed", error.message, $("#ddiMigrationAlert")); }
    finally { button.button("reset"); }
}
function importDdiMigration() {
    if (!ddiMigrationReviewToken) return;
    var button = $("#ddiMigrationImport"); button.button("loading");
    HTTPRequest({url: "api/settings/migration/import", method: "POST", token: sessionData.token, data: {review: ddiMigrationReviewToken},
        success: function () { button.button("reset"); resetDdiMigrationReview(); setTimeout(function () { button.prop("disabled", true); $("#ddiMigrationAlert").attr("tabindex", "-1").trigger("focus"); }, 0); showAlert("success", "Import complete", "DNS and DDI data were imported. DHCP scopes remain disabled. A recovery backup is retained on this server.", $("#ddiMigrationAlert"), true); refreshDnsSettings(true); },
        error: function () { button.button("reset"); ddiMigrationReviewToken = null; setTimeout(function () { button.prop("disabled", true); }, 0); },
        invalidToken: function () { button.button("reset"); ddiMigrationReviewToken = null; setTimeout(function () { button.prop("disabled", true); }, 0); showPageLogin(); },
        objAlertPlaceholder: $("#ddiMigrationAlert")});
}
