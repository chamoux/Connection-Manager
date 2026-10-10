// Mirrors ConnectionManager.Core.Ipc (plan "Shared contracts"). camelCase on the wire, enums as camelCase strings,
// DateTimeOffset as an ISO 8601 string. Keep this file in step with the C# records.

export type ModeSetting = 'auto' | 'on' | 'off'

export type Command =
  | 'getStatus' | 'subscribe'
  | 'setGameMode' | 'setDataSaver'
  | 'addBlock' | 'removeBlock'
  | 'addGame' | 'markNotAGame' | 'removeGame'
  | 'toggleCatalog'
  | 'allowApp' | 'removeAllowedApp'
  | 'setHotspot'
  | 'setMetered'
  | 'setOptions'
  | 'getUsage' | 'getReports' | 'dismissReport'
  | 'restoreEverything'
  | 'reportFullscreenGame'
  | 'setUploadCap' | 'removeUploadCap'

/** The tray reports full-screen games itself; the page never sends this one (Task 16 rejects it). */
export type UiCommand = Exclude<Command, 'reportFullscreenGame'>

/** Handled by the tray window itself, never forwarded to the service. */
export type BridgeOnlyCommand = 'pickProgram' | 'pickFolder' | 'openLogFolder'

export interface Request {
  v: 1
  id: number
  cmd: Command
  args?: unknown
}

/** id 0 is a pushed StatusDto. */
export interface Response<T = unknown> {
  id: number
  ok: boolean
  error?: string | null
  data?: T | null
}

export interface ModeArgs { mode: ModeSetting }
/** removeGame: isFolder means "from the always list". */
export interface PathArgs { path: string; isFolder?: boolean }
export interface IdArgs { id: string }
export interface HotspotArgs { name: string; isHotspot: boolean }
export interface OnArgs { on: boolean }
export interface GameArgs { pid: number; path: string }
/** C# `KBps` under the default camelCase policy. */
export interface UploadCapArgs { path: string; kBps: number }

export interface OptionsDto {
  graceMinutes: number
  notifications: boolean
  hotkey: string
  autoOnMeteredWifi: boolean
  allowWindowsDownloads: boolean
  windowsDownloadCapKBps: number
  wifiTuning: boolean
  trafficMeter: boolean
  detectNonSteamGames: boolean
  uploadCapsEnabled: boolean
}

export type NetworkKind = 'ethernet' | 'wifi' | 'none'

export interface NetworkDto {
  kind: NetworkKind
  name: string | null
  metered: boolean
  isHotspot: boolean
  hotspotReason: string | null
  canMarkMetered: boolean
  markedMeteredByUs: boolean
}

export interface CatalogItemDto { id: string; label: string; on: boolean }
export interface UserBlock { path: string; isFolder: boolean }

export interface SettingsDto {
  options: OptionsDto
  catalog: CatalogItemDto[]
  extraBlocks: UserBlock[]
  alwaysGames: string[]
  neverGames: string[]
  allowApps: string[]
  hotspotNetworks: string[]
  uploadCaps: UploadCapArgs[]
}

export interface ReportSummaryDto {
  id: string
  game: string
  endedAt: string
  totalBytes: number
  appCount: number
}

export interface StatusDto {
  version: number
  gameMode: ModeSetting
  gameActive: boolean
  gameName: string | null
  graceEndsAt: string | null
  runningGames: string[]
  blockedCount: number
  dataSaver: ModeSetting
  saverActive: boolean
  saverReason: string
  allowedCount: number
  network: NetworkDto
  problems: string[]
  settings: SettingsDto
  latestReport: ReportSummaryDto | null
}

export interface UsageDto {
  path: string
  name: string
  bytesIn: number
  bytesOut: number
}

export interface ReportDto {
  id: string
  game: string
  startedAt: string
  endedAt: string
  apps: UsageDto[]
}

/** Reply to pickProgram / pickFolder; the bridge returns null when the user cancels. */
export interface PickResult { path: string }

/** Service limits (Ipc/IpcLimits.cs, Qos/UploadCapManager.cs) and the OptionsDto ranges. The form checks the ranges before sending; the service enforces all of them. */
export const Limits = {
  graceMinutes: { min: 0, max: 30 },
  windowsDownloadCapKBps: { min: 16, max: 100_000 },
  uploadCapKBps: { min: 8, max: 1_000_000 },
  maxUploadCaps: 50,
  hotkeyMaxLength: 64,
  pathMaxLength: 1024,
  nameMaxLength: 256,
} as const
