import { Limits, type OptionsDto } from '../types'

export type OptionErrors = Partial<Record<keyof OptionsDto, string>>

/** A whole number from a text field, or NaN ("", "2.5", "abc"). */
export function parseWholeNumber(text: string): number {
  const trimmed = text.trim()
  return /^\d{1,9}$/.test(trimmed) ? Number(trimmed) : Number.NaN
}

const inRange = (value: number, range: { min: number; max: number }) =>
  Number.isInteger(value) && value >= range.min && value <= range.max

/** The checks the service makes too; doing them here gives the message before a round trip. The service has the last word on the hotkey. */
export function validateOptions(options: OptionsDto): OptionErrors {
  const errors: OptionErrors = {}
  const { graceMinutes, windowsDownloadCapKBps } = Limits

  if (!inRange(options.graceMinutes, graceMinutes))
    errors.graceMinutes = `Enter a whole number of minutes from ${graceMinutes.min} to ${graceMinutes.max}.`

  if (!inRange(options.windowsDownloadCapKBps, windowsDownloadCapKBps))
    errors.windowsDownloadCapKBps = `Enter a speed from ${windowsDownloadCapKBps.min} to ${windowsDownloadCapKBps.max.toLocaleString('en-US')} KB/s.`

  const hotkey = options.hotkey.trim()
  if (hotkey === '') errors.hotkey = 'Enter a hotkey, for example Ctrl+Alt+G.'
  else if (hotkey.length > Limits.hotkeyMaxLength) errors.hotkey = 'That hotkey is too long.'

  return errors
}

/** The first problem among the fields a change touches, so the message shows next to that control. */
export function firstError(errors: OptionErrors, fields: readonly (keyof OptionsDto)[]): string | null {
  for (const field of fields) {
    const message = errors[field]
    if (message) return message
  }
  return null
}
