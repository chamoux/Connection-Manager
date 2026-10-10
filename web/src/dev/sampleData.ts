// Sample data for pnpm dev (mockHost) and the tests. Never part of a production build.
import type { ReportDto, StatusDto, UsageDto } from '../types'

const MB = 1024 * 1024

/** Paths are written with / here for readability and turned into Windows paths. */
export const win = (path: string) => path.replaceAll('/', '\\')

export function sampleStatus(overrides: Partial<StatusDto> = {}): StatusDto {
  return {
    version: 1,
    gameMode: 'auto',
    gameActive: false,
    gameName: null,
    graceEndsAt: null,
    runningGames: [],
    blockedCount: 0,
    dataSaver: 'auto',
    saverActive: false,
    saverReason: 'On Ethernet',
    allowedCount: 2,
    network: {
      kind: 'ethernet',
      name: null,
      metered: false,
      isHotspot: false,
      hotspotReason: null,
      canMarkMetered: false,
      markedMeteredByUs: false,
    },
    problems: [],
    settings: {
      options: {
        graceMinutes: 2,
        notifications: true,
        hotkey: 'Ctrl+Alt+G',
        autoOnMeteredWifi: true,
        allowWindowsDownloads: false,
        windowsDownloadCapKBps: 200,
        wifiTuning: true,
        trafficMeter: true,
        detectNonSteamGames: true,
        uploadCapsEnabled: true,
      },
      catalog: [
        { id: 'google-updater', label: 'Google Updater', on: true },
        { id: 'edge-update', label: 'Microsoft Edge Update', on: true },
        { id: 'razer', label: 'Razer (Synapse, Cortex, updaters)', on: true },
        { id: 'windows-update', label: 'Windows Update and Delivery Optimization', on: true },
        { id: 'office', label: 'Microsoft Office updates', on: true },
        { id: 'adobe', label: 'Adobe updaters', on: true },
        { id: 'nvidia-app', label: 'NVIDIA app (driver downloads)', on: false },
      ],
      extraBlocks: [
        { path: win('C:/Program Files/Epic Games/Launcher/Portal/Binaries/Win64/EpicGamesLauncher.exe'), isFolder: false },
        { path: win('C:/Program Files (x86)/Overwolf'), isFolder: true },
      ],
      alwaysGames: [win('D:/Games/Riot Games/VALORANT/live/VALORANT.exe')],
      neverGames: [win('D:/SteamLibrary/steamapps/common/wallpaper_engine/wallpaper64.exe')],
      allowApps: [win('C:/Users/Player/AppData/Local/Discord/app-1.0.9200/Discord.exe'), win('C:/Program Files/Spotify/Spotify.exe')],
      hotspotNetworks: ['Pixel 8 Hotspot'],
      uploadCaps: [],
    },
    latestReport: null,
    ...overrides,
  }
}

export function sampleUsage(): UsageDto[] {
  return [
    { path: win('C:/Program Files/Google/Chrome/Application/chrome.exe'), name: 'chrome.exe', bytesIn: 1480 * MB, bytesOut: 62 * MB },
    { path: win('C:/Program Files (x86)/Steam/steam.exe'), name: 'steam.exe', bytesIn: 920 * MB, bytesOut: 8 * MB },
    { path: win('C:/Users/Player/AppData/Local/Discord/app-1.0.9200/Discord.exe'), name: 'Discord.exe', bytesIn: 210 * MB, bytesOut: 96 * MB },
    { path: win('C:/Program Files/Microsoft OneDrive/OneDrive.exe'), name: 'OneDrive.exe', bytesIn: 140 * MB, bytesOut: 310 * MB },
    { path: win('C:/Program Files/Spotify/Spotify.exe'), name: 'Spotify.exe', bytesIn: 88 * MB, bytesOut: 3 * MB },
    { path: win('C:/Program Files/NVIDIA Corporation/NVIDIA app/CEF/NVIDIA app.exe'), name: 'NVIDIA app.exe', bytesIn: 41 * MB, bytesOut: 1 * MB },
    { path: win('C:/Windows/System32/svchost.exe'), name: 'svchost.exe', bytesIn: 12 * MB, bytesOut: 2 * MB },
  ]
}

export function sampleReports(): ReportDto[] {
  return [
    {
      id: '6f1c2a4e-3b9d-4c55-9a51-0c1d2e3f4a5b',
      game: 'Hades II',
      startedAt: '2026-10-09T19:02:00+02:00',
      endedAt: '2026-10-09T21:14:00+02:00',
      apps: [
        { path: win('C:/Program Files/Microsoft OneDrive/OneDrive.exe'), name: 'OneDrive.exe', bytesIn: 12 * MB, bytesOut: 220 * MB },
        { path: win('C:/Program Files/NVIDIA Corporation/NVIDIA app/CEF/NVIDIA app.exe'), name: 'NVIDIA app.exe', bytesIn: 118 * MB, bytesOut: 1 * MB },
      ],
    },
    {
      id: '0a9b8c7d-6e5f-4a3b-2c1d-0e9f8a7b6c5d',
      game: 'Counter-Strike 2',
      startedAt: '2026-10-08T21:30:00+02:00',
      endedAt: '2026-10-08T23:05:00+02:00',
      apps: [{ path: win('C:/Program Files/Google/Chrome/Application/chrome.exe'), name: 'chrome.exe', bytesIn: 64 * MB, bytesOut: 2 * MB }],
    },
  ]
}
