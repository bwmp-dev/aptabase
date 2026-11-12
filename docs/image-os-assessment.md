# Final image OS vulnerability assessment

Assessed 2026-11-12. The image was not rebuilt or modified during this assessment.

- Local index identity: `sha256:c3e05c596bf555edd60c578525d3fe1c6e92c92a034e8d07562753d6ac748f9e` (linux/amd64).
- OCI platform manifest: `sha256:cd93fd14fa118a618096ea8574fede6e3d23734e8e457b1c3eafbfe53b3c7039`.
- Image config ID: `sha256:7cd6b758025e27049de6b3beb10891cf7885326075b855d900a6e4863d317b98`. Grype records the matching local repo digest and config ID; its Docker-export manifest digest differs from the OCI manifest. This is not a new candidate.

## Scanner and freshness

Docker Scout 1.22.0 refused before scanning because a Docker login was required. No login was attempted; that exit 1 is a scanner-access failure, not a clean result.

Official [Anchore Grype 0.121.3](https://github.com/anchore/grype/releases/tag/v0.121.3), released 2026-11-09, was downloaded to task-local temporary storage, with no system-wide installation. Zip SHA256 `3007a5afea25c60030a49d6c24d4e5abb6c94c0dbeed7ea303eb26df8e900c18` matched both the GitHub release asset digest and the official checksum file. The scan used Syft 1.57.1, one Go execution thread, a 240-second process deadline and the explicit local Docker source by exact image identity. Database update happened first; application update checks and external enrichment were disabled, then automatic database updates were disabled for the scan. No image/registry upload occurred.

Grype schema 6.1.10 database built `2026-11-12T06:20:58Z`, Ubuntu provider captured `2026-11-12T00:23:00Z`, database valid=true. Database archive SHA256 `c98bd6bd207590fa04c8e2fea3db1859f7f952b57400fc62e19f517f1b562f5e`; enforced maximum age 24 hours. Database update and scan both exited 0 with no scan errors. Exit 0 means completion because no severity exit threshold was configured; it does not mean zero findings. See [database documentation](https://oss.anchore.com/docs/guides/vulnerability/database/) and [configuration reference](https://oss.anchore.com/docs/reference/grype/configuration/).

Local evidence: `%TEMP%/aptabase-os-scan-c3e05c59/report.json`, `ubuntu-binary-triage.json`, `database-update.log`, `scan.log`. Report SHA256 `8f21bac64cde8d21148af79c9a6ecbcf8ab1e689efad83ff942a0ae82466ebe8`. Raw findings were retained without suppression.

## Results and applicability

**Not a clean scan:** 101 package/CVE matches, 0 ignored: 7 critical, 25 high, 54 medium, 13 low, 2 negligible under raw Grype severities.

- Ubuntu 24.04 package-aware matches: 47 across 25 unique CVEs (36 medium, 9 low, 2 negligible package/CVE matches). None lists an available fixed package version. These include unresolved, deferred and wont-fix cases, not 25 proven remotely exploitable application paths.
- Upstream binary-version matches: 54 for `/usr/bin/openssl`, `/usr/bin/getopt` and `/usr/bin/gzip`. All three are owned by the Ubuntu packages, and their actual checksums match package metadata. Installed versions: OpenSSL/libssl3t64 `3.0.13-0ubuntu3.16`, util-linux `2.39.3-9ubuntu6.6`, gzip `1.12-1ubuntu3.2`.
- For all 54, the same fresh database was queried for Ubuntu noble/default-channel records. 52 have Ubuntu fixes already installed (checked with the image's `dpkg --compare-versions`); 1 is vendor-not-affected; 1 remains unfixed and also appears in the Ubuntu package findings. This is evidence-based triage, not deletion of scanner results.

The remaining binary match is [CVE-2026-76642](https://ubuntu.com/security/CVE-2026-76642), a util-linux mount-helper/post-mount-hook local privilege issue. Ubuntu marks noble vulnerable, medium priority; its CVSS is high. Do not summarize this as no high-severity risk solely because the distro matcher uses medium. The app does not need privileged mount operations. Restricting container capabilities, privilege escalation and shell/local access is relevant mitigation, not a vendor fix or proof of non-exploitability. Actual production security options are for the parent to inspect; none were changed here.

Official spot checks corroborate backports: [CVE-2025-15467](https://ubuntu.com/security/CVE-2025-15467) fixed in 3.0.13-0ubuntu3.7 and [CVE-2024-5535](https://ubuntu.com/security/CVE-2024-5535) fixed in 3.0.13-0ubuntu3.2, both below installed 3.16. [CVE-2026-95818](https://ubuntu.com/security/CVE-2026-95818) is still needs-evaluation for noble and concerns the loader/setuid boundary. [CVE-2026-95210](https://ubuntu.com/security/CVE-2026-95210) is deferred and disputed by the GnuTLS supplier. Those pending records remain visible.

## Practical next action

No supported Ubuntu fixed version is listed for the 47 residual package matches, so there is no evidence-backed package upgrade to apply to this exact image now. Do not replace distro libraries with upstream binaries just to satisfy version-only matching. Preserve the digest, record residual-risk review, and update the pinned Microsoft base/rescan/retest when vendor fixes are published. A chiseled/minimal runtime or non-root/capability hardening could reduce exposure, but changes runtime/volume/diagnostic assumptions and needs separate acceptance; no such changes or permission changes were made. This scanner cannot establish exploitability, host-kernel security, live Coolify settings or future advisory coverage.

## Residual Ubuntu package matches

| CVE | Scanner severity | Status | Installed packages |
| --- | --- | --- | --- |
| [CVE-2016-20013](https://ubuntu.com/security/CVE-2016-20013) | Negligible | wont-fix | libc-bin, libc6 |
| [CVE-2016-2781](https://ubuntu.com/security/CVE-2016-2781) | Low | wont-fix | coreutils |
| [CVE-2022-3219](https://ubuntu.com/security/CVE-2022-3219) | Low | wont-fix | gpgv |
| [CVE-2024-10041](https://ubuntu.com/security/CVE-2024-10041) | Medium | wont-fix | libpam-modules, libpam-modules-bin, libpam-runtime, libpam0g |
| [CVE-2024-56433](https://ubuntu.com/security/CVE-2024-56433) | Low | not-fixed | login, passwd |
| [CVE-2025-5222](https://ubuntu.com/security/CVE-2025-5222) | Low | not-fixed | libicu74 |
| [CVE-2026-102473](https://ubuntu.com/security/CVE-2026-102473) | Medium | not-fixed | dash |
| [CVE-2026-102474](https://ubuntu.com/security/CVE-2026-102474) | Medium | not-fixed | dash |
| [CVE-2026-105712](https://ubuntu.com/security/CVE-2026-105712) | Low | not-fixed | gpgv |
| [CVE-2026-118131](https://ubuntu.com/security/CVE-2026-118131) | Medium | not-fixed | gpgv |
| [CVE-2026-18477](https://ubuntu.com/security/CVE-2026-18477) | Medium | not-fixed | tar |
| [CVE-2026-18508](https://ubuntu.com/security/CVE-2026-18508) | Medium | not-fixed | tar |
| [CVE-2026-40228](https://ubuntu.com/security/CVE-2026-40228) | Low | not-fixed | libsystemd0, libudev1, systemd |
| [CVE-2026-54369](https://ubuntu.com/security/CVE-2026-54369) | Medium | wont-fix | libacl1 |
| [CVE-2026-54370](https://ubuntu.com/security/CVE-2026-54370) | Medium | wont-fix | libacl1 |
| [CVE-2026-67693](https://ubuntu.com/security/CVE-2026-67693) | Medium | not-fixed | libgnutls30t64 |
| [CVE-2026-76642](https://ubuntu.com/security/CVE-2026-76642) | Medium | not-fixed | bsdutils, libblkid1, libmount1, libsmartcols1, libuuid1, mount, util-linux |
| [CVE-2026-78410](https://ubuntu.com/security/CVE-2026-78410) | Medium | not-fixed | bsdutils, libblkid1, libmount1, libsmartcols1, libuuid1, mount, util-linux |
| [CVE-2026-84243](https://ubuntu.com/security/CVE-2026-84243) | Medium | not-fixed | libc-bin, libc6 |
| [CVE-2026-88647](https://ubuntu.com/security/CVE-2026-88647) | Medium | not-fixed | libgnutls30t64 |
| [CVE-2026-88648](https://ubuntu.com/security/CVE-2026-88648) | Medium | not-fixed | libgnutls30t64 |
| [CVE-2026-95184](https://ubuntu.com/security/CVE-2026-95184) | Medium | not-fixed | libgnutls30t64 |
| [CVE-2026-95210](https://ubuntu.com/security/CVE-2026-95210) | Medium | not-fixed | libgnutls30t64 |
| [CVE-2026-95818](https://ubuntu.com/security/CVE-2026-95818) | Medium | not-fixed | libc-bin, libc6 |
| [CVE-2026-97399](https://ubuntu.com/security/CVE-2026-97399) | Medium | not-fixed | libc-bin, libc6 |

## Binary-match reconciliation

| Package | CVE | Assessment | Ubuntu fixed version |
| --- | --- | --- | --- |
| gzip | CVE-2026-41991 | vendor-fix-installed | 1.12-1ubuntu3.2 |
| gzip | CVE-2026-41992 | vendor-fix-installed | 1.12-1ubuntu3.2 |
| openssl | CVE-2024-13176 | vendor-fix-installed | 3.0.13-0ubuntu3.5 |
| openssl | CVE-2024-2511 | vendor-fix-installed | 3.0.13-0ubuntu3.2 |
| openssl | CVE-2024-4603 | vendor-fix-installed | 3.0.13-0ubuntu3.2 |
| openssl | CVE-2024-4741 | vendor-fix-installed | 3.0.13-0ubuntu3.2 |
| openssl | CVE-2024-5535 | vendor-fix-installed | 3.0.13-0ubuntu3.2 |
| openssl | CVE-2024-6119 | vendor-fix-installed | 3.0.13-0ubuntu3.4 |
| openssl | CVE-2024-9143 | vendor-fix-installed | 3.0.13-0ubuntu3.5 |
| openssl | CVE-2025-15467 | vendor-fix-installed | 3.0.13-0ubuntu3.7 |
| openssl | CVE-2025-68160 | vendor-fix-installed | 3.0.13-0ubuntu3.7 |
| openssl | CVE-2025-69418 | vendor-fix-installed | 3.0.13-0ubuntu3.7 |
| openssl | CVE-2025-69419 | vendor-fix-installed | 3.0.13-0ubuntu3.7 |
| openssl | CVE-2025-69420 | vendor-fix-installed | 3.0.13-0ubuntu3.7 |
| openssl | CVE-2025-69421 | vendor-fix-installed | 3.0.13-0ubuntu3.7 |
| openssl | CVE-2025-9230 | vendor-fix-installed | 3.0.13-0ubuntu3.6 |
| openssl | CVE-2026-22795 | vendor-fix-installed | 3.0.13-0ubuntu3.7 |
| openssl | CVE-2026-22796 | vendor-fix-installed | 3.0.13-0ubuntu3.7 |
| openssl | CVE-2026-28387 | vendor-fix-installed | 3.0.13-0ubuntu3.9 |
| openssl | CVE-2026-28388 | vendor-fix-installed | 3.0.13-0ubuntu3.9 |
| openssl | CVE-2026-28389 | vendor-fix-installed | 3.0.13-0ubuntu3.9 |
| openssl | CVE-2026-28390 | vendor-fix-installed | 3.0.13-0ubuntu3.9 |
| openssl | CVE-2026-31789 | vendor-fix-installed | 3.0.13-0ubuntu3.9 |
| openssl | CVE-2026-31790 | vendor-fix-installed | 3.0.13-0ubuntu3.9 |
| openssl | CVE-2026-34180 | vendor-fix-installed | 3.0.13-0ubuntu3.11 |
| openssl | CVE-2026-34182 | vendor-fix-installed | 3.0.13-0ubuntu3.11 |
| openssl | CVE-2026-35189 | vendor-fix-installed | 3.0.13-0ubuntu3.16 |
| openssl | CVE-2026-42766 | vendor-fix-installed | 3.0.13-0ubuntu3.11 |
| openssl | CVE-2026-42767 | vendor-fix-installed | 3.0.13-0ubuntu3.11 |
| openssl | CVE-2026-42770 | vendor-fix-installed | 3.0.13-0ubuntu3.11 |
| openssl | CVE-2026-45445 | vendor-fix-installed | 3.0.13-0ubuntu3.11 |
| openssl | CVE-2026-45446 | vendor-fix-installed | 3.0.13-0ubuntu3.11 |
| openssl | CVE-2026-45447 | vendor-fix-installed | 3.0.13-0ubuntu3.11 |
| openssl | CVE-2026-54872 | vendor-fix-installed | 3.0.13-0ubuntu3.16 |
| openssl | CVE-2026-54874 | vendor-fix-installed | 3.0.13-0ubuntu3.15 |
| openssl | CVE-2026-63072 | vendor-fix-installed | 3.0.13-0ubuntu3.15 |
| openssl | CVE-2026-63074 | vendor-fix-installed | 3.0.13-0ubuntu3.15 |
| openssl | CVE-2026-63076 | vendor-fix-installed | 3.0.13-0ubuntu3.15 |
| openssl | CVE-2026-7383 | vendor-fix-installed | 3.0.13-0ubuntu3.11 |
| openssl | CVE-2026-75803 | vendor-fix-installed | 3.0.13-0ubuntu3.15 |
| openssl | CVE-2026-75805 | vendor-fix-installed | 3.0.13-0ubuntu3.16 |
| openssl | CVE-2026-75806 | vendor-fix-installed | 3.0.13-0ubuntu3.16 |
| openssl | CVE-2026-77696 | vendor-fix-installed | 3.0.13-0ubuntu3.16 |
| openssl | CVE-2026-84782 | vendor-fix-installed | 3.0.13-0ubuntu3.16 |
| openssl | CVE-2026-9076 | vendor-fix-installed | 3.0.13-0ubuntu3.11 |
| util-linux | CVE-2024-28085 | vendor-fix-installed | 2.39.3-9ubuntu6 |
| util-linux | CVE-2026-13595 | vendor-fix-installed | 2.39.3-9ubuntu6.6 |
| util-linux | CVE-2026-27456 | vendor-fix-installed | 2.39.3-9ubuntu6.6 |
| util-linux | CVE-2026-3184 | vendor-not-affected | ? |
| util-linux | CVE-2026-53612 | vendor-fix-installed | 2.39.3-9ubuntu6.6 |
| util-linux | CVE-2026-53613 | vendor-fix-installed | 2.39.3-9ubuntu6.6 |
| util-linux | CVE-2026-53614 | vendor-fix-installed | 2.39.3-9ubuntu6.6 |
| util-linux | CVE-2026-53615 | vendor-fix-installed | 2.39.3-9ubuntu6.6 |
| util-linux | CVE-2026-76642 | not-fixed | ? |
