import { readonly, ref, type Ref } from 'vue'
import { enUSMessages } from './catalog'

export const STORAGE_KEY = 'igw.web.locale'
export type IgwLocale = 'zh-CN' | 'en-US'
export type TranslationParams = Record<string, string | number>

export interface LocaleStorage {
  getItem(key: string): string | null
  setItem(key: string, value: string): void
}

export interface DocumentLanguageTarget {
  lang: string
}

export interface I18nRuntime {
  locale: Readonly<Ref<IgwLocale>>
  setLocale(next: IgwLocale): void
  t(source: string, params?: TranslationParams): string
}

export function normalizeLocale(value: unknown): IgwLocale {
  return value === 'en-US' ? 'en-US' : 'zh-CN'
}

export function interpolate(text: string, params: TranslationParams = {}): string {
  return text.replace(/\{(\w+)\}/g, (_, key: string) => String(params[key] ?? `{${key}}`))
}

function safelyReadLocale(storage?: LocaleStorage): IgwLocale {
  try { return normalizeLocale(storage?.getItem(STORAGE_KEY)) }
  catch { return 'zh-CN' }
}

export function createI18nRuntime(
  storage?: LocaleStorage,
  documentElement?: DocumentLanguageTarget,
  englishMessages: Readonly<Record<string, string>> = enUSMessages,
): I18nRuntime {
  const current = ref<IgwLocale>(safelyReadLocale(storage))
  if (documentElement) documentElement.lang = current.value

  function setLocale(next: IgwLocale) {
    current.value = normalizeLocale(next)
    if (documentElement) documentElement.lang = current.value
    try { storage?.setItem(STORAGE_KEY, current.value) } catch { /* private mode may reject storage */ }
  }

  function t(source: string, params: TranslationParams = {}) {
    const translated = current.value === 'en-US' ? englishMessages[source] ?? source : source
    return interpolate(translated, params)
  }

  return { locale: readonly(current), setLocale, t }
}

const browserStorage = typeof window === 'undefined' ? undefined : window.localStorage
const browserDocument = typeof document === 'undefined' ? undefined : document.documentElement

export const i18n = createI18nRuntime(browserStorage, browserDocument)

