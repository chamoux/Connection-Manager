const units = ['B', 'KB', 'MB', 'GB'] as const

/** 1024-based, like Windows: "512 B", "1.5 KB", "120 MB", "2.3 GB". One decimal below 10, whole numbers above. */
export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B'
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  if (unit === 0) return `${Math.round(value)} B`
  if (Math.round(value) >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  // 9.96 KB would round to "10.0 KB"; show it as "10 KB" instead.
  const text = value < 9.95 ? value.toFixed(1) : Math.round(value).toLocaleString('en-US')
  return `${text} ${units[unit]}`
}

/** "1:05" for 65 s; never negative. */
export function formatCountdown(ms: number): string {
  const total = Math.max(0, Math.ceil(ms / 1000))
  const minutes = Math.floor(total / 60)
  const seconds = total % 60
  return `${minutes}:${seconds.toString().padStart(2, '0')}`
}

const dateTime = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })
const timeOnly = new Intl.DateTimeFormat(undefined, { timeStyle: 'short' })

export function formatDateTime(iso: string): string {
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? '' : dateTime.format(date)
}

/** "9 Oct 2026, 20:10 to 21:45", with the date once when both ends fall on the same day. */
export function formatRange(startIso: string, endIso: string): string {
  const start = new Date(startIso)
  const end = new Date(endIso)
  if (Number.isNaN(start.getTime()) || Number.isNaN(end.getTime())) return ''
  const sameDay = start.toDateString() === end.toDateString()
  return `${dateTime.format(start)} to ${sameDay ? timeOnly.format(end) : dateTime.format(end)}`
}

export function plural(count: number, one: string, many = `${one}s`): string {
  return `${count.toLocaleString('en-US')} ${count === 1 ? one : many}`
}

/** The last part of a Windows path: "C:\Games\x.exe" → "x.exe". */
export function fileName(path: string): string {
  const trimmed = path.replace(/[\\/]+$/, '')
  const cut = Math.max(trimmed.lastIndexOf('\\'), trimmed.lastIndexOf('/'))
  return cut < 0 ? trimmed : trimmed.slice(cut + 1)
}

/** Windows compares paths and network names ignoring case. */
export const sameText = (a: string, b: string) => a.localeCompare(b, undefined, { sensitivity: 'accent' }) === 0

export const includesText = (list: readonly string[], value: string) => list.some(item => sameText(item, value))
