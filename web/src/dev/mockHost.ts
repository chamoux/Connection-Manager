// pnpm dev only: plays the tray window and the service in the browser, with sample data, so every page works.
// Add ?offline to the URL to see the "Service not running" banner. Never part of a production build.
import type { WebViewLike, WebViewMessageEvent } from '../bridge'
import type { ModeSetting, OptionsDto, StatusDto } from '../types'
import { sampleReports, sampleStatus, sampleUsage, win } from './sampleData'

type Listener = (event: WebViewMessageEvent) => void
interface Message { id: number; cmd: string; args?: Record<string, unknown> }

const hotkeyPattern = /^((ctrl|control|alt|shift|win)\+){1,4}([a-z0-9]|f([1-9]|1\d|2[0-4]))$/i
const samePath = (a: string, b: string) => a.toLowerCase() === b.toLowerCase()
const without = (list: string[], value: string) => list.filter(item => !samePath(item, value))

export function createMockHost(): WebViewLike {
  const listeners = new Set<Listener>()
  const offline = new URLSearchParams(location.search).has('offline')
  let status: StatusDto = sampleStatus({
    gameMode: 'auto',
    gameActive: true,
    gameName: 'Hades2.exe',
    blockedCount: 9,
    runningGames: [win('D:/SteamLibrary/steamapps/common/Hades II/Ship/Hades2.exe')],
    dataSaver: 'auto',
    saverActive: true,
    saverReason: 'Your hotspot "Pixel 8 Hotspot"',
    network: {
      kind: 'wifi', name: 'Pixel 8 Hotspot', metered: true, isHotspot: true,
      hotspotReason: 'In your hotspot list', canMarkMetered: false, markedMeteredByUs: false,
    },
    problems: ['Wi-Fi scan pause could not be applied: the Wi-Fi adapter refused the setting.'],
    latestReport: { id: sampleReports()[0]!.id, game: 'Hades II', endedAt: '2026-10-09T21:14:00+02:00', totalBytes: 351 * 1024 * 1024, appCount: 2 },
  })
  const reports = sampleReports()

  const emit = (data: unknown) => listeners.forEach(listener => listener({ data }))

  function change(update: (current: StatusDto) => StatusDto) {
    status = update(structuredClone(status))
    status.version++
    status.allowedCount = status.settings.allowApps.length
    setTimeout(() => emit({ id: 0, ok: true, data: structuredClone(status) }), 30)
  }

  function setMode(key: 'gameMode' | 'dataSaver', mode: ModeSetting) {
    change(s => {
      s[key] = mode
      if (key === 'gameMode') s.gameActive = mode === 'on' || (mode === 'auto' && s.runningGames.length > 0)
      else s.saverActive = mode === 'on' || (mode === 'auto' && s.network.isHotspot)
      return s
    })
  }

  function handle({ cmd, args = {} }: Message): unknown {
    const path = typeof args.path === 'string' ? args.path : ''
    switch (cmd) {
      case 'getStatus': return structuredClone(status)
      case 'getUsage': return sampleUsage()
      case 'getReports': return reports
      case 'setGameMode': setMode('gameMode', args.mode as ModeSetting); return null
      case 'setDataSaver': setMode('dataSaver', args.mode as ModeSetting); return null
      case 'toggleCatalog':
        change(s => ({ ...s, settings: { ...s.settings, catalog: s.settings.catalog.map(c => (c.id === args.id ? { ...c, on: !c.on } : c)) } }))
        return null
      case 'addBlock':
        if (status.settings.extraBlocks.some(b => samePath(b.path, path))) throw new Error('That program is already on the list.')
        change(s => { s.settings.extraBlocks.push({ path, isFolder: args.isFolder === true }); return s })
        return null
      case 'removeBlock':
        change(s => { s.settings.extraBlocks = s.settings.extraBlocks.filter(b => !samePath(b.path, path)); return s })
        return null
      case 'addGame':
        change(s => { s.settings.neverGames = without(s.settings.neverGames, path); s.settings.alwaysGames = [...without(s.settings.alwaysGames, path), path]; return s })
        return null
      case 'markNotAGame':
        change(s => {
          s.settings.alwaysGames = without(s.settings.alwaysGames, path)
          s.settings.neverGames = [...without(s.settings.neverGames, path), path]
          s.runningGames = without(s.runningGames, path)
          return s
        })
        return null
      case 'removeGame':
        change(s => { if (args.isFolder) s.settings.alwaysGames = without(s.settings.alwaysGames, path); else s.settings.neverGames = without(s.settings.neverGames, path); return s })
        return null
      case 'allowApp':
        change(s => { s.settings.allowApps = [...without(s.settings.allowApps, path), path]; return s })
        return null
      case 'removeAllowedApp':
        change(s => { s.settings.allowApps = without(s.settings.allowApps, path); return s })
        return null
      case 'setHotspot': {
        const name = String(args.name ?? '')
        change(s => { s.settings.hotspotNetworks = args.isHotspot ? [...without(s.settings.hotspotNetworks, name), name] : without(s.settings.hotspotNetworks, name); return s })
        return null
      }
      case 'setMetered':
        if (!status.network.canMarkMetered && !status.network.markedMeteredByUs) throw new Error('Windows already treats this network as metered.')
        change(s => { s.network.metered = args.on === true; s.network.markedMeteredByUs = args.on === true; return s })
        return null
      case 'setOptions': {
        const options = args as unknown as OptionsDto
        if (!hotkeyPattern.test(options.hotkey.replaceAll(' ', ''))) throw new Error('Hotkey not valid.')
        change(s => { s.settings.options = options; return s })
        return null
      }
      case 'dismissReport':
        change(s => { s.latestReport = null; return s })
        return null
      case 'restoreEverything':
        change(s => { s.gameMode = 'auto'; s.dataSaver = 'auto'; s.network.markedMeteredByUs = false; return s })
        return null
      case 'setUploadCap': {
        const kBps = Number(args.kBps)
        if (!Number.isInteger(kBps) || kBps < 8 || kBps > 1_000_000) throw new Error('An upload cap must be 8 to 1,000,000 KB/s.')
        change(s => {
          const index = s.settings.uploadCaps.findIndex(c => samePath(c.path, path))
          if (index < 0) s.settings.uploadCaps.push({ path, kBps })
          else s.settings.uploadCaps[index] = { path, kBps }
          return s
        })
        return null
      }
      case 'removeUploadCap':
        change(s => { s.settings.uploadCaps = s.settings.uploadCaps.filter(c => !samePath(c.path, path)); return s })
        return null
      case 'pickProgram': return { path: win('C:/Program Files/Example/Updater/updater.exe') }
      case 'pickFolder': return { path: win('C:/Program Files/Example') }
      case 'openLogFolder': return null
      default: throw new Error('Unknown command.')
    }
  }

  return {
    postMessage(raw) {
      if (offline) return
      const message = raw as Message
      setTimeout(() => {
        try {
          emit({ id: message.id, ok: true, error: null, data: handle(message) ?? null })
        } catch (error) {
          emit({ id: message.id, ok: false, error: error instanceof Error ? error.message : 'Failed.', data: null })
        }
      }, 150)
    },
    addEventListener: (_type, listener) => { listeners.add(listener) },
    removeEventListener: (_type, listener) => { listeners.delete(listener) },
  }
}
