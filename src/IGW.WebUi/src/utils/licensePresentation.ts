export type LicenseTranslate = (
  source: string,
  params?: Record<string, string | number>,
) => string

export interface LicensePresentationSource {
  status?: string | null
  customer?: string | null
  expiry?: string | null
  daysLeft?: number | null
  message?: string | null
}

const statusLabels: Record<string, string> = {
  Valid: '授权有效',
  Trial: '7 天试用中',
  ExpiringSoon: '即将到期',
  Grace: '已过期（宽限期内）',
  Expired: '已过期（已停止采集）',
  TrialExpired: '试用已结束（已停止采集）',
  NotActivated: '未激活',
  MachineMismatch: '授权码与本机不符',
  Tampered: '系统时间异常',
  Invalid: '授权码无效',
}

export function licenseStatusLabel(
  license: LicensePresentationSource | null | undefined,
  translate: LicenseTranslate,
): string {
  const status = license?.status || ''
  const label = statusLabels[status]
  return label ? translate(label) : status || '—'
}

export function licenseCustomerName(customer: string | null | undefined, translate: LicenseTranslate): string {
  if (!customer) return ''
  return customer === '本机试用' ? translate('本机试用') : customer
}

export function licenseStatusMessage(
  license: LicensePresentationSource | null | undefined,
  translate: LicenseTranslate,
): string {
  if (!license) return translate('正在读取授权状态…')
  const date = license.expiry || '—'
  const days = license.daysLeft ?? 0
  switch (license.status) {
    case 'Valid':
      return license.expiry
        ? translate('授权有效期到 {date}。', { date })
        : translate('授权有效。')
    case 'ExpiringSoon':
      return translate('授权将在 {date} 到期（剩 {days} 天），请及时续期。', { date, days })
    case 'Trial':
      return translate('7 天试用中，到期 {date}（剩 {days} 天）。试用结束后需使用授权码激活。', { date, days })
    case 'Grace':
      return translate('授权已于 {date} 到期，当前处于宽限期，请尽快续期。', { date })
    case 'Expired':
      return translate('授权已过期，采集与北向服务已停止，请使用新授权码激活。')
    case 'TrialExpired':
      return translate('本机 7 天试用已结束，请使用授权码激活。')
    case 'NotActivated':
      return translate('尚未激活，请使用授权码激活。')
    case 'MachineMismatch':
      return translate('授权码与本机不匹配，请使用当前机器码重新申请授权。')
    case 'Tampered':
      return translate('检测到系统时间异常，请校准系统时间后重试。')
    case 'Invalid':
      return translate('授权码无效，请检查后重新激活。')
    default:
      return license.message || translate('授权状态不可用')
  }
}
