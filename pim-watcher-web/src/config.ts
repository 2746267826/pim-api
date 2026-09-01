const config = {
  isDevelopment: import.meta.env.DEV,
  heartbeat: {
    alarmName: 'heartbeat',
    intervalInSeconds: 60,
  },
}

export default config
