import { inject, type App, type InjectionKey } from 'vue'
import { i18n, type I18nRuntime, type TranslationParams } from './runtime'

const I18nKey: InjectionKey<I18nRuntime> = Symbol('IGW i18n')

export function installI18n(app: App) {
  app.provide(I18nKey, i18n)
  app.config.globalProperties.$t = i18n.t
}

export function useI18n(): I18nRuntime {
  return inject(I18nKey, i18n)
}

export * from './runtime'

declare module 'vue' {
  interface ComponentCustomProperties {
    $t(source: string, params?: TranslationParams): string
  }
}
