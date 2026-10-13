import { createApp } from 'vue'
import ElementPlus from 'element-plus'
import 'element-plus/dist/index.css'
import { createPinia } from 'pinia'
import App from './App.vue'
import router from './router'
import { api } from './api'
import { i18n, installI18n, normalizeLocale } from './i18n'

const app = createApp(App)
app.use(createPinia())
app.use(router)
app.use(ElementPlus)
installI18n(app)

async function restoreServerLocale() {
  try {
    const preferences = await api.uiPreferences()
    i18n.setLocale(normalizeLocale(preferences.locale))
  } catch {
    // Runtime unavailable during startup: keep the browser-local fallback and let the page open normally.
  }
}

async function bootstrap() {
  await restoreServerLocale()
  app.mount('#app')
}

void bootstrap()
