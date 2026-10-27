# DualNet — Engineering Context and AI Handoff

**Read this file first.** It is the project's durable knowledge base, written to be
**self-contained and decoupled from any conversation history**. A new AI session or a new
maintainer should be able to resume work using only this file plus the repository itself.
Do not assume that any earlier chat context exists or is authoritative — this file is.

| | |
| --- | --- |
| Product | **DualNet** (双网通) — portable Windows network-adapter routing assistant |
| Version | 1.3.1 |
| Platform | Windows 10 / 11 x64, .NET 8 (WPF), requires administrator rights |
| Repository | git, branch `main`, normal incremental commits; remote `https://github.com/ecLieKing/DualNet` |
| User manual | `README.md` (Chinese) |
| License | MIT — see `LICENSE` |

---

## 1. What DualNet is

DualNet is a Windows desktop utility that routes selected traffic through a chosen network
adapter. It originated as a way to reach industrial equipment over a dedicated Ethernet cable
while keeping internet access on a second adapter, and grew into a general-purpose
"partial traffic / full traffic" routing assistant.

The application:

- saves a baseline of the machine's network configuration before its first change,
- supports rollback on failure and one-click restoration at any time,
- runs a 60-second independent watchdog in the high-risk mode,
- stores all state under `%LocalAppData%\DualNet` so the portable EXE needs no neighbouring files,
- does **not** share the internet connection with devices, and does **not** require a
  connectivity check as a precondition for applying configuration.

## 2. Repository conventions

- **Working directory is the repository root** (the folder containing `DualNet.sln`).
- Branch is `main`. History grows through **normal incremental commits**: each logical
  change is committed on top of the previous one with `git commit`. Do **not** amend or
  rewrite commits that already exist; if a correction is needed, make a follow-up commit.
- The repository-local commit identity is the owner's GitHub account (`ecLieKing`), so pushes
  attribute correctly on GitHub. The canonical remote is `https://github.com/ecLieKing/DualNet`.
  Keep hostnames, machine paths and third-party personal data out of tracked files, and check
  before every push.
- The project files (`*.csproj`) contain Chinese literals (`AssemblyName`, `Product`,
  `Description`) and are encoded **UTF-8 without BOM**. Preserve that encoding; do not let an
  editor rewrite them as ANSI/GBK or add a BOM.
- `outputs/` and `work/.dotnet/`, `work/publish-stage/` are git-ignored build/delivery artifacts.
  `work/UiSmoke/` is deliberately **not** ignored and is part of the source tree.
- **不要再生成压缩包 — never create, keep, upload or hand over compressed archives** (`.zip`, `.7z`,
  `.rar`, `.tar`, `.tar.gz`). Do not pack deliverables, releases, source snapshots or intermediate
  hand-offs; deliver plain files and directories. This rule overrides the ZIP step in §7.
- A pre-migration working copy of this project may still exist outside the repository. Never
  develop, commit or publish from that copy. Remove it only after confirming nothing holds a
  lock on it.

## 3. Product behavior (the contract)

### 3.1 Partial traffic

- Choose **any visible target adapter** (Ethernet, Wi-Fi, USB/cellular, VPN or virtual).
- Add either a **single device IPv4** (which creates an explicit `/32` route) or a **CIDR subnet**
  (for example `192.168.10` or `192.168.10.0/24`).
- If the target adapter already has a local address in the same subnet, it is **reused**. If not,
  the app automatically picks an unused address and adds it with a **`/24`** prefix.
- Optionally designate a separate **internet adapter**: any other adapter that already has a
  Windows default route. It only becomes the *preferred egress for other IPv4 default traffic*.
  It must differ from the target adapter. This setting does **not** test or guarantee real
  internet connectivity, and is never mandatory.
- Device presence is determined by an **ARP response from the chosen source address**. Ping is a
  diagnostic only; addresses that merely answer Ping are not presented as real devices.

### 3.2 Full traffic (strict mode)

- Choose one target adapter and set or reuse its IPv4 address, prefix, gateway and optional DNS.
- IPv4 and IPv6 on **all other visible adapters are temporarily disabled**.
- After applying, the user **must confirm within 60 seconds**. Otherwise an independent watchdog
  process restores the baseline.
- This mode can disconnect the machine instantly. Never apply it on a real machine merely to
  validate UI or automation.

### 3.3 Invariants

These must hold after any change:

1. The original baseline is written once and is **never overwritten** by subsequent applies.
2. A failed apply attempts to roll back; the user can always click "restore original network".
3. The target adapter does not have to be wired, and connectivity is never asserted as a
   precondition.
4. No drivers are installed, Internet Connection Sharing is never enabled, and industrial devices
   are never reconfigured.
5. The UI must never claim that other addresses inside the same `/24` definitely will not egress
   through the target adapter.

## 4. Architecture and code map

| Responsibility | Location |
| --- | --- |
| Window layout, rule rows, confirmation banner, bottom action bar | `src/DualNet.App/MainWindow.xaml` |
| Theme resources and shared control templates | `src/DualNet.App/App.xaml`, `Themes/Light.xaml`, `Themes/Dark.xaml`, `Themes/ThemeManager.cs` |
| UI state, commands, add/remove rules, apply/restore interaction | `src/DualNet.App/ViewModels/MainViewModel.cs`, `RoutingRuleViewModel.cs` |
| Mode / rule / profile / adapter / snapshot data models | `src/DualNet.Core/Models/` |
| Argument parsing, CIDR maths, adapter recommendation | `src/DualNet.Core/Networking/` |
| Network inventory, configuration transaction, snapshots, 60-second watchdog | `src/DualNet.Core/Services/`, especially `NetworkConfigurationService.cs`, `SnapshotStore.cs`, `WatchdogCoordinator.cs` |
| Automated tests | `tests/DualNet.Tests/` |
| Simulated XAML preview / binding smoke check | `work/UiSmoke/` |
| Packaging script | `scripts/publish.ps1` |

- The window logo is a vector `DrawingImage` in `App.xaml`; the EXE and taskbar icon is
  `src/DualNet.App/Assets/app.ico`.
- The watchdog is the same EXE re-entered with `--watchdog <operationId>`; see `App.xaml.cs`.
- Target frameworks: `net8.0` for `DualNet.Core`, `net8.0-windows` for the app and the smoke project.

## 5. Local data, privacy and safety

All machine-specific state lives in `%LocalAppData%\DualNet\` and is **outside the repository**:

| File | Purpose |
| --- | --- |
| `baseline.json` | Original network baseline captured before the first change |
| `pending.json` | An applied-but-unconfirmed operation awaiting recovery |
| `profile.json` | Saved routing rules |
| `ui-settings.json` | Interface preferences (e.g. `darkTheme`) |
| `watchdog-<id>.armed` / `watchdog-<id>.confirmed` | 60-second watchdog state |
| `dualnet.log` | Runtime log |

Rules:

- **Never delete or rewrite the recovery baseline** on a user's machine.
- **Never commit** these files, real network configuration, or machine identifiers.
- The application performs **no outbound network requests**: there is no HTTP client, no telemetry
  and no update check. Its network activity is limited to local PowerShell/CIM queries about
  adapters and routes, plus ARP requests and diagnostic Pings to user-specified local targets.
  Keep it that way.
- Applying or restoring configuration genuinely changes routes, DHCP, IP addresses or protocol
  bindings, and full-traffic mode disables other adapters. Prefer the simulated executor and the
  UI smoke check during development; real network writes must be explicitly arranged by the user
  and must always preserve a recovery path.

## 6. Development and verification

Run from the repository root (ensure the .NET 8 SDK's `dotnet` is on `PATH`):

```powershell
dotnet test .\DualNet.sln -c Release
dotnet build .\src\DualNet.App\DualNet.App.csproj -c Release
dotnet run --project .\work\UiSmoke\UiSmoke.csproj -c Release --no-restore
```

Documented acceptance result for the v1.3.1 baseline (plus the unreleased ComboBox text fix):

- 50 test cases pass,
- Release build completes with 0 warnings / 0 errors,
- the UI smoke check prints `COMBOBOX_TOGGLE=OK (3)`, `COMBOBOX_ITEM_TEXT=OK (2x3)` twice (dark and
  light) and `UI_BINDING_INITIALIZATION=OK`,
- the smoke also writes `work/ui-dropdown-dark-*.png` / `work/ui-dropdown-light-*.png`, which show an
  open adapter drop-down with every row's text, the selected-row highlight and the long-name ellipsis.

Preview images are written into the ignored `work/` directory. The smoke program never performs a
real network apply or restore. Because it does not call `Application.Run()`, the first dispatcher pump
triggers `App.OnStartup`; the smoke lets that happen up front and immediately closes the window it
creates, so later screenshots keep the theme they asked for.

Change requirements:

- Any change to the network transaction logic **must add matching failure and recovery tests**.
- Any UI change must be checked against: light **and** dark theme, mouse interaction with the
  combo boxes, the 900 px minimum window width, the empty-rule state, and the 60-second
  confirmation banner.

## 7. Release process

```powershell
.\scripts\publish.ps1 -DotNetExe $dotnet -Version '1.3.1'
```

- The script publishes a Windows x64 **self-contained single-file** EXE, writes a SHA-256
  manifest, and packs a ZIP.
- The release package contains **only** `双网通.exe` and `SHA256SUMS.txt`. The README is
  deliberately **not** copied into the release directory or ZIP.
- The script **overwrites** the local release directory and ZIP of the same version. Run it only
  when a release is actually intended.
- After publishing, verify the EXE version, the ZIP file listing and the SHA-256 value.
- If behavior changes, decide a new version number first and update it consistently in the project
  file, the application manifest, the README and the publish script.
- The application is **not code-signed**. The Windows SmartScreen "unknown publisher" warning is a
  known, accepted delivery limitation.

## 8. Retired designs — do not reintroduce

These were tried and deliberately removed. They are history, not a backlog:

1. **Three-step configuration wizard plus a concurrent whole-subnet Ping scan.** It listed many
   addresses that were not real devices. Replaced by direct device-IP / subnet rules, with ARP from
   the selected source address as the existence check and Ping as diagnostics only.
2. **Downscaled complex bitmap logo.** It was unreadable in the window and taskbar. Replaced by a
   simple vector `DrawingImage` plus a multi-size ICO. Do not go back to a blurry scaled PNG.
3. **Treating "has a default route" as "must reach some test website".** This produced false
   "online check failed" reports when a default route existed. Internet probing was removed
   entirely. The designated internet adapter only marks a preferred egress and guarantees nothing.
4. **Forcing an industrial wired adapter plus a separate internet adapter.** This excluded USB
   tethering, cellular, Wi-Fi, VPN and virtual adapters. Any Windows-visible adapter is now
   selectable; disconnected adapters can be configured but raise a risk notice.
5. **Assigning the host address as `/32` on the industrial adapter**, which did not match real
   device-subnet usage. Auto-added host addresses now use `/24`, while single-device targets still
   get a precise `/32` route. The UI must not overstate the isolation of the rest of the `/24`.
6. **Empty DNS entries on USB-tethered and Hyper-V adapters**, which once caused a null reference
   during baseline restore; and re-enabling DHCP after adding a static helper address used to delete
   that address. Empty DNS entries are now filtered, and during split routing the DHCP lease's
   address, gateway and DNS are temporarily frozen and then restored.
7. **Multiple per-adapter CIM queries and repeated full adapter reads while updating routes**, which
   left the user with no feedback for a long time. Status and progress are now shown per stage, only
   adapters that were actually modified are restored, and restore/apply commands are merged.
8. **A self-drawn ComboBox that could only be opened with Alt+Down** after the v1.3.0 redesign, plus
   a status column that was too narrow at 900 px and a stale count in the empty preview. Fixed in
   v1.3.1 (mouse drop-down, narrow layout, empty state).
9. **Adapter drop-down rows that rendered as blank stripes.** The custom `ComboBoxItem` template
   painted `{TemplateBinding Content}` as its text. Items come from `ItemsSource` as `AdapterInfo`
   records, and that binding performs no object-to-string conversion, so every row was an empty
   `TextBlock` inside a popup whose background matched the card behind it. Rows now render through
   `ContentPresenter` plus the shared `AdapterItemTemplate`, the popover uses the input surface colour,
   the selected row uses the dark `ItemSelectedBrush`, and long names are ellipsized with a tooltip.
   New combo boxes must reuse `AdapterItemTemplate`; never paint `Content` directly as text.

Preserve what these mistakes replaced: straightforward rule editing, clear status feedback,
one-click restore, and the 60-second confirmation in the high-risk mode.

## 9. UI glossary (Chinese UI → English)

| UI string | Meaning |
| --- | --- |
| 双网通 | DualNet (product / window title) |
| 路由方式 | Routing mode |
| 部分流量 | Partial traffic |
| 全部流量 / 全部流量（严格模式） | Full traffic / full traffic (strict mode) |
| 高风险 | High risk |
| 目标网卡 | Target adapter |
| 指定上网网卡（可选） | Designate internet adapter (optional) |
| 刷新网络 | Refresh network |
| 分流目标 | Routing targets |
| 单个设备 IP / 整个网段 | Single device IP / whole subnet |
| 添加目标 | Add target |
| 本机地址（可修改） | Local address (editable) |
| 移除 | Remove |
| 应用部分流量 / 应用全部流量 | Apply partial traffic / apply full traffic |
| 更新路由 | Update routes |
| 保留此配置 | Keep this configuration |
| 立即恢复 / 恢复原网络 | Restore now / restore original network |
| 本机 IPv4 / 前缀 / 路由器地址 | Local IPv4 / prefix / router address (gateway) |
| 可选 DNS（多个地址用逗号分隔） | Optional DNS (comma-separated) |
| 还没有分流目标 | No routing targets yet |
