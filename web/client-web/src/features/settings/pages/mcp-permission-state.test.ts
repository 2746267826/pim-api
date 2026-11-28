import { describe, expect, it } from 'vitest'

import { initialMcpPermissions } from './mcp-permission-state'

describe('initialMcpPermissions', () => {
  it('keeps native-note reads closed when an old client has no key', () => {
    expect(
      initialMcpPermissions(
        [
          { name: 'get_tasks', defaultEnabled: true },
          { name: 'get_native_notes', defaultEnabled: false },
        ],
        { get_tasks: true },
      ),
    ).toEqual({ get_tasks: true, get_native_notes: false })
  })

  it('drops unknown saved read tools instead of carrying them forward', () => {
    expect(
      initialMcpPermissions(
        [
          { name: 'get_tasks', defaultEnabled: true },
          { name: 'get_native_notes', defaultEnabled: false },
        ],
        { notes_read: true, get_native_note: true, get_native_notes: true },
      ),
    ).toEqual({ get_tasks: true, get_native_notes: true })
  })

  it('defaults write tools to disabled when the catalog has no default', () => {
    expect(initialMcpPermissions([{ name: 'delete_native_note' }], undefined)).toEqual({
      delete_native_note: false,
    })
  })
})
