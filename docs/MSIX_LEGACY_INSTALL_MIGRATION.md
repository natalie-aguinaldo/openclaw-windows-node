# Migrating Legacy Inno Installs to MSIX

## Decision

Existing OpenClaw Companion installs use Inno Setup, not NSIS. When a user
installs the Store MSIX over an existing Inno installation, reconciling the two
installations is the product's responsibility. The user must not have to
discover or manually resolve competing Companion installations.

The packaged Companion owns detection and orchestration through a dedicated
app-package migration service. SetupEngine continues to own local gateway
provisioning and repair. It must not own app-package migration.

MSIX installation cannot depend on arbitrary custom install-time code.
Migration therefore runs on the packaged app's first launch, before ordinary
startup, single-instance activation, background work, tray creation, protocol
handling, gateway connections, or startup-task registration.

This document defines the required product behavior. It does not implement the
migration service or change either installer.

## Ownership

| Area | Owner |
|---|---|
| Legacy install detection, consent, migration state, and rollback | Packaged Companion migration service |
| Packaged startup, protocol, tray, and update ownership | Packaged Companion |
| Legacy binary and registration removal | Verified Inno uninstaller in migration-preserve mode |
| Existing settings, gateway registry, credentials, and logs | Migration service, with the current app data stores remaining authoritative |
| Existing local WSL gateway | Preserved during migration |
| Gateway creation, setup, repair, and explicit removal | SetupEngine |
| MSIX installation and updates | Microsoft Store and Windows package infrastructure |

## Exact legacy install detection

Detection must identify the supported Inno installation, not merely something
that resembles OpenClaw. The migration service must start from the expected
current-user Inno uninstall registry record for the known OpenClaw Companion
AppId. The stable AppId in `installer.iss` is
`{M0LTB0T-TRAY-4PP1-D3N7}`. Development installs use a distinct AppId and must
not be treated as the stable product.

Before offering migration, validate the uninstall record and its trusted
metadata, including:

- the exact AppId-backed uninstall registry key;
- expected product display name and publisher;
- a normalized install location owned by that uninstall record;
- an uninstall command and uninstaller located within that install location;
- supported installed version and architecture metadata;
- an acceptable Authenticode signature on the uninstaller and relevant legacy
  binaries.

If any required evidence is absent or contradictory, do not infer an install
from directory names, process names, shortcuts, protocol registrations, WSL
distro names, or loose files. Report that automatic migration is unavailable
and provide manual recovery guidance.

## Startup gate and consent

Until migration reaches a verified terminal state, the packaged app may show
only the migration and recovery experience. It must not run normally alongside
the legacy Companion.

When an exact legacy install is detected, show an explicit explanation of:

- why two installations cannot remain active;
- which Companion installation, settings, credentials, and gateway were found;
- which application integrations will move to the packaged app;
- which state will be preserved;
- how retry and rollback work.

The recommended action is **Migrate and remove the old Companion**. Never
silently uninstall user software.

The user may defer. Deferral must leave the legacy Companion usable and make no
destructive changes, but the packaged app must then exit or remain limited to
the migration experience. Deferral must not create a second tray icon, connect
to the gateway, claim startup, handle `openclaw:` links, or claim update
ownership.

## Migration transaction

Migration must be resumable and journaled. Persist each completed phase so an
interrupted launch can retry or roll back without guessing.

1. **Detect.** Validate the exact Inno install and record its version,
   architecture, install location, uninstaller, running processes, and owned
   registrations.
2. **Explain and obtain consent.** Do not continue until the user selects
   **Migrate and remove the old Companion**.
3. **Back up and preserve state.** Preserve settings, the gateway registry,
   per-gateway identities and credentials, logs as appropriate, and the
   existing `OpenClawGateway` WSL gateway. Record hashes and locations needed
   for verification and rollback. Do not export secrets into a less protected
   location.
4. **Close the legacy tray.** Ask it to exit through a supported shutdown path,
   wait for termination, and verify that no legacy Companion instance remains.
   If it cannot be closed, stop before uninstalling anything.
5. **Validate adoption.** From the packaged process context, prove that the
   packaged app can read and use the existing state and can reconnect using the
   existing gateway registry and credential precedence. Do not mutate or
   downgrade a paired device token during this check.
6. **Prepare rollback.** Ensure a verified recovery path can restore the legacy
   Companion binaries and registrations if a later step fails. Do not enter
   the destructive phase without this recovery path.
7. **Remove the legacy Companion.** Invoke the verified Inno uninstaller using
   the migration-specific preserve-state contract. Remove only legacy binaries,
   shortcuts, autostart entries, protocol registration, and legacy updater
   ownership. Preserve the WSL gateway and all user and gateway state.
8. **Transfer ownership.** Enable packaged startup only if the user previously
   enabled startup, establish packaged protocol ownership, and rely on the
   Store for packaged update ownership. Do not leave legacy updater tasks or
   registrations active.
9. **Verify postconditions.** Confirm that only the packaged Companion can
   provide tray presence, startup, protocol handling, gateway connections, and
   updates. Confirm that settings, credentials, logs selected for preservation,
   and the existing WSL gateway remain available.
10. **Commit or roll back.** Mark migration complete only after all
    postconditions pass. Otherwise restore the legacy Companion and its
    registrations, preserve user and gateway state, explain the failure, and
    offer retry or manual recovery.

Normal packaged startup may proceed only after the transaction commits.

## Required Inno migration contract

The current `installer.iss` behavior on `main` blocks safe migration. During a
silent uninstall, `EnsureLocalGatewayCleanupChoice` defaults
`LocalGatewayCleanupRequested` to true and automatically runs
`Uninstall-LocalGateway.ps1`. That path removes the local WSL gateway and
generated state. Calling the existing silent uninstaller from a migration flow
is therefore unsafe.

Before runtime migration can ship, the Inno uninstaller must support an
explicit migration switch, named `/MIGRATIONPRESERVESTATE`, with this contract:

- it is accepted by the supported legacy uninstaller and recorded in its log;
- it suppresses all local gateway and generated user-state cleanup, including
  the current silent-uninstall default;
- it preserves settings, gateway registry records, identities, credentials,
  logs selected for preservation, and the `OpenClawGateway` WSL distro;
- it still removes only the old Companion binaries, shortcuts, autostart,
  protocol registration, and updater ownership;
- it returns an unambiguous nonzero exit code when the contract cannot be
  honored or removal is incomplete;
- it supports post-uninstall verification and a tested rollback path.

The migration service must verify that the installed uninstaller supports this
contract before offering automatic removal. It must never substitute ordinary
`/SILENT` or `/VERYSILENT` uninstall behavior.

Adding this switch and implementing the migration service are required runtime
work, but are intentionally out of scope for this documentation-only change.

## Failure and recovery

Failure must be fail-safe. Do not report success unless all postconditions
pass. A failure before the destructive phase leaves the legacy install
untouched. A failure after that boundary restores the legacy binaries and
registrations from the verified recovery path. In every case, settings,
credentials, and the local WSL gateway remain intact.

The failure UI must identify the failed phase, preserve diagnostic logs, and
offer **Retry migration** and clear manual recovery instructions. It must not
fall through to ordinary packaged startup or leave both Companions running.

## MSIX uninstall and the local gateway

MSIX uninstall has no arbitrary uninstall hook and cannot automatically clean
external WSL state. Migration therefore preserves the existing local gateway,
and later removal of the MSIX also leaves that gateway intact.

**Remove Local Gateway** remains a separate, explicit, destructive user action
owned by SetupEngine. Removing the Companion package must never imply consent
to remove the user's local gateway.

## Acceptance criteria

- [ ] An existing supported Inno installation is detected from the exact
  AppId-backed uninstall record and validated install metadata. Broad
  heuristics do not trigger migration.
- [ ] No legacy install is called NSIS in product UI, diagnostics,
  documentation, or tests.
- [ ] The packaged app displays explicit consent with **Migrate and remove the
  old Companion** as the recommended action and never silently uninstalls it.
- [ ] Deferring migration leaves the legacy install and all state intact while
  preventing normal packaged tray, connection, startup, protocol, background,
  and update behavior.
- [ ] Settings, gateway registry records, credentials, appropriate logs, and
  the existing WSL gateway survive successful migration without credential
  downgrade or re-pairing.
- [ ] At most one Companion owns tray presence, startup, `openclaw:` protocol
  handling, gateway connections, and update behavior throughout the supported
  flow.
- [ ] The verified legacy Inno uninstaller supports
  `/MIGRATIONPRESERVESTATE`; ordinary silent uninstall is rejected for
  migration.
- [ ] Both x64 and ARM64 Inno-to-MSIX migrations pass on supported Windows
  systems, including architecture-correct detection and uninstall validation.
- [ ] After migration, Microsoft Store MSIX updates retain adopted state, do
  not rerun destructive migration, and do not restore legacy updater ownership.
- [ ] Failure injection at every transaction phase proves that user and gateway
  state remain intact, no false success is reported, and rollback or retry is
  available.
- [ ] Startup and protocol ownership transfer is verified across sign-out,
  reboot, activation by protocol, and app update.
- [ ] Removing the MSIX leaves external WSL state intact, and **Remove Local
  Gateway** remains a separate explicit action.
- [ ] Current-head, signed x64 and ARM64 artifacts pass the complete migration,
  rollback, Store update, protocol, startup, and no-dual-running scenarios on
  real Windows hosts. Mocks and manifest inspection alone are not release
  proof.
