export const MIMO_FLASH_MODEL = 'mimo-v2.6-flash'
export const MIMO_PRO_MODEL = 'mimo-v2.6-pro'
export const MIMO_MULTIMODAL_PRESET_ID = 'mimo-multimodal'

export function isMimoPro(model: string | null | undefined) {
  return model === MIMO_PRO_MODEL
}

export function asrModelOptionLabel(model: string) {
  if (model === MIMO_FLASH_MODEL) return `${model}（推荐，长音频默认）`
  if (model === MIMO_PRO_MODEL) return `${model}（Pro，不推荐长音频）`
  return model
}

export function asrAudioLimitLabel(maxBytes: number) {
  if (!Number.isFinite(maxBytes) || maxBytes <= 0) return '大小限制未知'
  const mib = maxBytes / 1024 / 1024
  return `单音频最多 ${Number.isInteger(mib) ? mib.toFixed(0) : mib.toFixed(1)} MiB`
}
