# Third party notices

## Public Suffix List

`DnsServerCore/Security/public_suffix_list.dat` is sourced from the [Public Suffix List](https://publicsuffix.org/list/public_suffix_list.dat). Its source header declares the Mozilla Public License 2.0; the original header and attribution are preserved. The license is available at [Mozilla](https://mozilla.org/MPL/2.0/).

The bundled dataset identifies version `2026-10-07_07-28-19_UTC` and source commit `3929462652695bad04f0a27afb600974014a3c8b`. Its SHA256 is `ba7d836c0ea57a8bcf9f1fcb868066a7554bed029b441ecc6204ad8494a32612`. Maintainers should update the bundled list from the official download and rerun suffix tests; this implementation does not download it during DNS queries.
