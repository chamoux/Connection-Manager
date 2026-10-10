import { describe, expect, it } from 'vitest'
import { sampleStatus } from '../dev/sampleData'
import type { NetworkDto } from '../types'
import { dataSaverView, gameModeView, networkView } from './statusText'

const now = Date.parse('2026-10-09T20:00:00Z')

describe('gameModeView', () => {
  it('waits for a game in Auto', () => {
    expect(gameModeView(sampleStatus())).toMatchObject({ label: 'Waiting for a game', tone: 'idle' })
  })

  it('names the game and the block count while it runs', () => {
    const view = gameModeView(sampleStatus({ gameActive: true, gameName: 'Hades.exe', blockedCount: 7 }), now)
    expect(view).toEqual({ label: 'On', detail: 'On for Hades.exe: 7 blocked.', tone: 'active' })
  })

  it('says when it was turned on by hand', () => {
    const view = gameModeView(sampleStatus({ gameMode: 'on', gameActive: true, blockedCount: 3 }), now)
    expect(view.detail).toBe('Turned on by hand: 3 blocked.')
  })

  it('counts down the grace period after the game closes', () => {
    const graceEndsAt = new Date(now + 85_000).toISOString()
    const view = gameModeView(sampleStatus({ gameActive: true, gameName: 'Hades.exe', graceEndsAt, blockedCount: 7 }), now)
    expect(view).toMatchObject({ label: 'Ending soon', tone: 'active' })
    expect(view.detail).toBe('Game closed. Turns off in 1:25. 7 blocked.')
  })

  it('ignores a grace end that has passed', () => {
    const graceEndsAt = new Date(now - 1000).toISOString()
    expect(gameModeView(sampleStatus({ gameActive: true, gameName: 'x.exe', graceEndsAt }), now).label).toBe('On')
  })

  it('shows Off when the user turned it off', () => {
    expect(gameModeView(sampleStatus({ gameMode: 'off' }))).toMatchObject({ label: 'Off', tone: 'off' })
  })
})

describe('dataSaverView', () => {
  it('waits for the hotspot in Auto and passes on the reason', () => {
    expect(dataSaverView(sampleStatus())).toMatchObject({ label: 'Waiting for your hotspot', tone: 'idle', note: 'On Ethernet' })
  })

  it('counts the allowed apps when on', () => {
    const view = dataSaverView(sampleStatus({ saverActive: true, allowedCount: 1, saverReason: 'Your hotspot "Pixel 8"' }))
    expect(view).toMatchObject({ label: 'On', tone: 'active', note: 'Your hotspot "Pixel 8"' })
    expect(view.detail).toBe('Only the apps you choose can use the internet. 1 app allowed.')
  })

  it('says when nothing is allowed yet', () => {
    expect(dataSaverView(sampleStatus({ saverActive: true, allowedCount: 0 })).detail).toMatch(/No apps allowed yet/)
  })

  it('drops an empty reason', () => {
    expect(dataSaverView(sampleStatus({ dataSaver: 'off', saverReason: ' ' }))).toMatchObject({ label: 'Off', tone: 'off', note: undefined })
  })
})

describe('networkView', () => {
  const base: NetworkDto = sampleStatus().network

  it('names Ethernet', () => {
    expect(networkView(base)).toEqual({ title: 'Ethernet', kind: 'Ethernet', tags: [], hotspotNote: null })
  })

  it('shows a hotspot with its reason and the metered flag', () => {
    const view = networkView({
      ...base, kind: 'wifi', name: 'iPhone', isHotspot: true, hotspotReason: 'iPhone hotspot', metered: true, markedMeteredByUs: true,
    })
    expect(view).toEqual({
      title: 'iPhone', kind: 'Wi-Fi', tags: ['Hotspot', 'Metered (set by Connection Manager)'], hotspotNote: 'iPhone hotspot',
    })
  })

  it('falls back when the Wi-Fi name is hidden', () => {
    expect(networkView({ ...base, kind: 'wifi', name: null }).title).toBe('Wi-Fi network')
  })

  it('shows no network', () => {
    expect(networkView({ ...base, kind: 'none', metered: true })).toEqual({ title: 'Not connected', kind: 'No network', tags: [], hotspotNote: null })
  })
})
