import { describe, expect, it } from 'vitest'
import { fileName, formatBytes, formatCountdown, includesText, plural } from './format'

describe('formatBytes', () => {
  it.each([
    [0, '0 B'],
    [1, '1 B'],
    [1023, '1023 B'],
    [1024, '1.0 KB'],
    [1536, '1.5 KB'],
    [10 * 1024, '10 KB'],
    [5 * 1024 * 1024, '5.0 MB'],
    [120 * 1024 * 1024, '120 MB'],
    [2.3 * 1024 ** 3, '2.3 GB'],
    [1536 * 1024 ** 3, '1,536 GB'],
  ])('%d bytes is %s', (bytes, text) => {
    expect(formatBytes(bytes)).toBe(text)
  })

  it('moves up a unit instead of showing 1,024', () => {
    expect(formatBytes(1024 * 1024 - 1)).toBe('1.0 MB')
    expect(formatBytes(10.2 * 1024)).toBe('10 KB')
  })

  it('shows nonsense as 0 B', () => {
    expect(formatBytes(-5)).toBe('0 B')
    expect(formatBytes(Number.NaN)).toBe('0 B')
    expect(formatBytes(Number.POSITIVE_INFINITY)).toBe('0 B')
  })
})

describe('formatCountdown', () => {
  it('shows minutes and seconds, rounding up', () => {
    expect(formatCountdown(65_000)).toBe('1:05')
    expect(formatCountdown(120_000)).toBe('2:00')
    expect(formatCountdown(400)).toBe('0:01')
  })
  it('never goes negative', () => expect(formatCountdown(-3000)).toBe('0:00'))
})

describe('helpers', () => {
  it('fileName takes the last part of a Windows path', () => {
    expect(fileName('C:\\Games\\Hades\\Hades.exe')).toBe('Hades.exe')
    expect(fileName('C:\\Program Files\\Razer\\')).toBe('Razer')
    expect(fileName('plain.exe')).toBe('plain.exe')
  })
  it('includesText ignores case like Windows', () => {
    expect(includesText(['C:\\Apps\\Discord.exe'], 'c:\\apps\\discord.EXE')).toBe(true)
    expect(includesText(['Pixel 8'], 'Pixel 9')).toBe(false)
  })
  it('plural picks the word form', () => {
    expect(plural(1, 'app')).toBe('1 app')
    expect(plural(3, 'app')).toBe('3 apps')
    expect(plural(1200, 'app')).toBe('1,200 apps')
  })
})
