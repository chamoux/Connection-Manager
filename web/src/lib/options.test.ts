import { describe, expect, it } from 'vitest'
import type { OptionsDto } from '../types'
import { firstError, parseWholeNumber, validateOptions } from './options'

const valid: OptionsDto = {
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
}

describe('validateOptions', () => {
  it('accepts the defaults', () => expect(validateOptions(valid)).toEqual({}))

  it.each([0, 30])('accepts %d grace minutes', graceMinutes => {
    expect(validateOptions({ ...valid, graceMinutes }).graceMinutes).toBeUndefined()
  })

  it.each([-1, 31, 2.5, Number.NaN])('rejects %d grace minutes', graceMinutes => {
    expect(validateOptions({ ...valid, graceMinutes }).graceMinutes).toMatch(/0 to 30/)
  })

  it.each([16, 100_000])('accepts a %d KB/s cap', windowsDownloadCapKBps => {
    expect(validateOptions({ ...valid, windowsDownloadCapKBps }).windowsDownloadCapKBps).toBeUndefined()
  })

  it.each([15, 100_001, 0, 12.5, Number.NaN])('rejects a %d KB/s cap', windowsDownloadCapKBps => {
    expect(validateOptions({ ...valid, windowsDownloadCapKBps }).windowsDownloadCapKBps).toMatch(/16 to 100,000 KB\/s/)
  })

  it.each(['', '   '])('rejects an empty hotkey (%j)', hotkey => {
    expect(validateOptions({ ...valid, hotkey }).hotkey).toMatch(/Enter a hotkey/)
  })

  it('rejects a hotkey longer than the service allows', () => {
    expect(validateOptions({ ...valid, hotkey: 'Ctrl+'.repeat(13) + 'G' }).hotkey).toMatch(/too long/)
  })

  it('leaves the hotkey format to the service', () => {
    expect(validateOptions({ ...valid, hotkey: 'Banana' }).hotkey).toBeUndefined()
  })
})

describe('firstError', () => {
  it('reports only the fields the change touches', () => {
    const errors = validateOptions({ ...valid, graceMinutes: 99, hotkey: '' })
    expect(firstError(errors, ['wifiTuning'])).toBeNull()
    expect(firstError(errors, ['hotkey'])).toMatch(/hotkey/)
  })
})

describe('parseWholeNumber', () => {
  it('reads whole numbers', () => {
    expect(parseWholeNumber('2')).toBe(2)
    expect(parseWholeNumber(' 200 ')).toBe(200)
  })
  it.each(['', 'abc', '2.5', '-1', '1e3'])('gives NaN for %j', text => {
    expect(parseWholeNumber(text)).toBeNaN()
  })
})
