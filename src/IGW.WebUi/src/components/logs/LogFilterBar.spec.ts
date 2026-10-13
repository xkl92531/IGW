import { shallowMount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { defineComponent, h } from 'vue'
import { buildLogQuery, createLogFilters, type LogFilters } from '../../utils/logCenter'
import type { LogFilterOptionsDto } from '../../api'
import LogFilterBar from './LogFilterBar.vue'

const options: LogFilterOptionsDto = {
  protocols: [{ id: 'Modbus.Tcp', name: 'Modbus TCP' }, { id: 'Siemens.S7', name: '西门子 S7' }],
  channels: [{ name: 'PLC8', protocol: 'Siemens.S7' }, { name: 'Line2', protocol: 'Modbus.Tcp' }],
  devices: [
    { channel: 'PLC8', name: 'OP205', path: 'PLC8.OP205' },
    { channel: 'PLC8', name: 'OP210', path: 'PLC8.OP210' },
    { channel: 'Line2', name: 'Pump', path: 'Line2.Pump' },
  ],
  groups: [
    { name: 'StationEntrance-16', path: 'PLC8.OP205.StationEntrance-16', channel: 'PLC8', device: 'PLC8.OP205', taskId: 't16', taskName: 'StationEntrance-16' },
    { name: 'Heart', path: 'Line2.Pump.Heart', channel: 'Line2', device: 'Line2.Pump', taskId: 'heart', taskName: 'Heart' },
  ],
}

// 下拉框、输入提示只保留 props / 事件，方便检查选项和模拟选择
const Select = defineComponent({ name: 'ElSelect', props: ['modelValue'], emits: ['update:modelValue'], setup: (_, { slots }) => () => h('div', slots.default?.()) })
const Option = defineComponent({ name: 'ElOption', props: ['label', 'value'], setup: () => () => h('i') })
const Autocomplete = defineComponent({ name: 'ElAutocomplete', props: ['modelValue', 'fetchSuggestions'], emits: ['update:modelValue', 'select'], setup: () => () => h('div') })
const Picker = defineComponent({ name: 'ElDatePicker', props: ['modelValue'], emits: ['update:modelValue', 'visible-change'], setup: () => () => h('div') })

function mountBar(modelValue: LogFilters = createLogFilters()) {
  const wrapper = shallowMount(LogFilterBar, {
    props: { modelValue, options },
    global: { stubs: { ElSelect: Select, ElOption: Option, ElAutocomplete: Autocomplete, ElDatePicker: Picker, ElButton: true } },
  })
  const select = (name: string) => wrapper.findAllComponents(Select).find(item => item.attributes('data-filter') === name)!
  const values = (name: string) => select(name).findAllComponents(Option).map(item => item.props('value'))
  const emitted = () => { const all = wrapper.emitted('update:modelValue')!; return all[all.length - 1][0] as LogFilters }
  return { wrapper, select, values, emitted }
}

describe('LogFilterBar', () => {
  it('only offers protocol, channel, device, tag group and time', () => {
    const { wrapper, values } = mountBar()
    expect(wrapper.findAllComponents(Select).map(item => item.attributes('data-filter'))).toEqual(['protocol', 'channel', 'device'])
    expect(wrapper.findComponent(Autocomplete).attributes('data-filter')).toBe('group')
    expect(wrapper.findComponent(Picker).exists()).toBe(true)
    expect(wrapper.find('[data-testid="advanced-filter-toggle"]').exists()).toBe(false)   // 关键词、级别、高级筛选都去掉了
    expect(values('protocol')).toEqual(['Modbus.Tcp', 'Siemens.S7'])
    expect(values('channel')).toEqual(['PLC8', 'Line2'])
    expect(values('device')).toEqual(['PLC8.OP205', 'PLC8.OP210', 'Line2.Pump'])
  })

  it('narrows channels by protocol and devices by channel, and choosing a device fills its channel', async () => {
    const { values, select, emitted, wrapper } = mountBar(createLogFilters({ protocol: 'Siemens.S7' }))
    expect(values('channel')).toEqual(['PLC8'])
    expect(values('device')).toEqual(['PLC8.OP205', 'PLC8.OP210'])

    await wrapper.setProps({ modelValue: createLogFilters({ channel: 'Line2', device: 'Line2.Pump' }) })
    expect(values('device')).toEqual(['Line2.Pump'])
    select('channel').vm.$emit('update:modelValue', 'PLC8')
    expect(emitted()).toMatchObject({ channel: 'PLC8', device: '' })      // 换通道：不属于它的设备清掉

    await wrapper.setProps({ modelValue: createLogFilters({ path: 'PLC6.D1' }) })
    select('device').vm.$emit('update:modelValue', 'PLC8.OP210')
    expect(emitted()).toMatchObject({ channel: 'PLC8', device: 'PLC8.OP210', path: '' })   // 下拉选了就不再用地址带来的工程位置
  })

  it('suggests enabled tag groups within the chosen scope and queries by the bound task', async () => {
    const { wrapper, emitted } = mountBar(createLogFilters({ channel: 'PLC8' }))
    const group = wrapper.findComponent(Autocomplete)
    const suggestions = (text: string) => new Promise<any[]>(resolve => group.props('fetchSuggestions')(text, resolve))
    expect((await suggestions('')).map(item => item.name)).toEqual(['StationEntrance-16'])   // 只提示所选通道下的
    expect(await suggestions('heart')).toEqual([])

    group.vm.$emit('select', options.groups[0])
    expect(emitted()).toMatchObject({ group: 'StationEntrance-16', task: 't16', path: '' })

  })

  it.each(['Station', '/api/report', 'mes.example', 'report'])('queries a typed name or URL fragment through the same attribute filter: %s', text => {
    const { wrapper, emitted } = mountBar(createLogFilters({ group: 'old', task: 'old-task', path: 'PLC8.OP205' }))
    wrapper.findComponent(Autocomplete).vm.$emit('update:modelValue', ` ${text} `)
    const query = buildLogQuery(emitted(), 1, 50)
    expect(query.taskOrInterface).toBe(text)
    expect(query).not.toHaveProperty('task')
    expect(query).not.toHaveProperty('path')
  })

  it('clears the typed filter without leaving the previous task active', () => {
    const { wrapper, emitted } = mountBar(createLogFilters({ group: 'Station', task: 't16' }))
    wrapper.findComponent(Autocomplete).vm.$emit('update:modelValue', '')
    const query = buildLogQuery(emitted(), 1, 50)
    expect(query).not.toHaveProperty('taskOrInterface')
    expect(query).not.toHaveProperty('task')
  })

  it('does not move the time range under the user while the picker is open', async () => {
    const filters = createLogFilters({}, Date.parse('2026-10-08T03:00:00Z'))
    const { wrapper } = mountBar(filters)
    const picker = () => wrapper.findComponent(Picker)
    const shown = () => (picker().props('modelValue') as Date[]).map(date => date.toISOString())
    const original = shown()

    picker().vm.$emit('visible-change', true)                 // 用户打开了时间面板，正在选
    await wrapper.setProps({ modelValue: { ...filters, from: '2026-10-08T02:00:10.000Z', to: '2026-10-08T03:00:10.000Z' } })
    expect(shown()).toEqual(original)                         // 每 10 秒的滑动不能改掉正在选的时间

    picker().vm.$emit('visible-change', false)                // 关掉面板后跟上最新时间
    await wrapper.vm.$nextTick()
    expect(shown()).toEqual(['2026-10-08T02:00:10.000Z', '2026-10-08T03:00:10.000Z'])
  })
})
