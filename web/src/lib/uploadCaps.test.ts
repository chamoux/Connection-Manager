import { describe, expect, it } from 'vitest'
import { checkUploadCapKBps, formatKBps } from './uploadCaps'

describe('checkUploadCapKBps', () => {
  it.each([
    ['8', 8],
    ['500', 500],
    [' 1000 ', 1000],
    ['1000000', 1_000_000],
  ])('accepts %j', (text, kBps) => {
    expect(checkUploadCapKBps(text)).toEqual({ kBps, error: null })
  })

  it.each(['7', '0', '1000001', '99999999', '', '   ', 'abc', '12.5', '-8', '1e3', '1,000'])('rejects %j', text => {
    const check = checkUploadCapKBps(text)
    expect(check.kBps).toBeNull()
    expect(check.error).toBe('Enter a speed from 8 to 1,000,000 KB/s.')
  })
})

describe('formatKBps', () => {
  it('groups the digits', () => {
    expect(formatKBps(8)).toBe('8 KB/s')
    expect(formatKBps(1_000_000)).toBe('1,000,000 KB/s')
  })
})
