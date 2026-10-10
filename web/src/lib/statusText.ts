import type { ModeSetting, NetworkDto, StatusDto } from '../types'
import { formatCountdown, plural } from './format'

/** active: the mode is working now. idle: waiting for its trigger. off: turned off by the user. */
export type Tone = 'active' | 'idle' | 'off'

export interface ModeView {
  label: string
  detail: string
  tone: Tone
  /** The service's own reason, shown under the detail when there is one. */
  note?: string
}

export const modeLabels: Record<ModeSetting, string> = { auto: 'Auto', on: 'On', off: 'Off' }

type GameFields = Pick<StatusDto, 'gameMode' | 'gameActive' | 'gameName' | 'graceEndsAt' | 'blockedCount'>

export function gameModeView(status: GameFields, now: number = Date.now()): ModeView {
  const blocked = `${status.blockedCount.toLocaleString('en-US')} blocked`

  if (status.gameActive) {
    const graceEnd = status.graceEndsAt ? Date.parse(status.graceEndsAt) : Number.NaN
    if (graceEnd > now)
      return { label: 'Ending soon', detail: `Game closed. Turns off in ${formatCountdown(graceEnd - now)}. ${blocked}.`, tone: 'active' }
    if (status.gameName) return { label: 'On', detail: `On for ${status.gameName}: ${blocked}.`, tone: 'active' }
    return { label: 'On', detail: `Turned on by hand: ${blocked}.`, tone: 'active' }
  }

  switch (status.gameMode) {
    case 'off':
      return { label: 'Off', detail: 'Updaters can use the internet, even during games.', tone: 'off' }
    case 'on':
      return { label: 'Starting', detail: 'Setting up the blocks.', tone: 'idle' }
    default: // auto
      return { label: 'Waiting for a game', detail: 'Turns on by itself when a game starts.', tone: 'idle' }
  }
}

type SaverFields = Pick<StatusDto, 'dataSaver' | 'saverActive' | 'saverReason' | 'allowedCount'>

export function dataSaverView(status: SaverFields): ModeView {
  const note = status.saverReason.trim() || undefined

  if (status.saverActive) {
    const allowed = status.allowedCount === 0 ? 'No apps allowed yet' : `${plural(status.allowedCount, 'app')} allowed`
    return { label: 'On', detail: `Only the apps you choose can use the internet. ${allowed}.`, tone: 'active', note }
  }

  switch (status.dataSaver) {
    case 'off':
      return { label: 'Off', detail: 'Every app can use the internet, even on your hotspot.', tone: 'off', note }
    case 'on':
      return { label: 'Starting', detail: 'Setting up the allowed apps.', tone: 'idle', note }
    default: // auto
      return { label: 'Waiting for your hotspot', detail: 'Turns on by itself when you connect to your hotspot.', tone: 'idle', note }
  }
}

export interface NetworkView {
  title: string
  kind: string
  tags: string[]
  /** Why it counts as a hotspot, when it does. */
  hotspotNote: string | null
}

export function networkView(network: NetworkDto): NetworkView {
  const tags: string[] = []
  if (network.isHotspot) tags.push('Hotspot')
  if (network.metered) tags.push(network.markedMeteredByUs ? 'Metered (set by Connection Manager)' : 'Metered')

  const hotspotNote = network.isHotspot ? network.hotspotReason?.trim() || null : null

  switch (network.kind) {
    case 'ethernet':
      return { title: network.name?.trim() || 'Ethernet', kind: 'Ethernet', tags, hotspotNote }
    case 'wifi':
      return { title: network.name?.trim() || 'Wi-Fi network', kind: 'Wi-Fi', tags, hotspotNote }
    default: // none
      return { title: 'Not connected', kind: 'No network', tags: [], hotspotNote: null }
  }
}
