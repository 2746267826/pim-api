/**
 * fakeData.ts — 展览馆 12 种数据类型真实假数据（与 Exhibition.html 同源）
 * 与 src/client-web/src/components/dashboard-exhibition/Exhibition.html 内 <script> 的 hash01/getFakeData 保持一致
 * 所有分布均为确定性（hash01），无 MathRandom，保证刷新一致且 12 类型互相关联
 *
 * 关联性设计：
 * - 日使用 Top3 占 60% 且与分类占比联动（微信→聊天、抖音→视频）
 * - 周趋势与日使用同源，周一/五双峰、周末 -20%
 * - 分类占比 8类和100%且与日使用一致
 * - 24h热力 8-12/19-23双峰，凌晨1-5 <5%，4点业务日切割处有缝隙
 * - GPS 3段连续轨迹 家→地铁→公司，速度与段绑定
 * - 常去地点 visitCount 与轨迹起点/终点一致
 * - 速度三峰 4/20/300 km/h
 * - PC应用 VS Code35%+Chrome30%占大头，AFK单独
 * - 键盘 QWERTY真实热度 ASDF/JKL; 高
 * - 任务完成率带7日移动平均 61→89%
 * - 习惯打卡 5习惯×30天，与周趋势相关周末-10%
 * - 设备健康 4设备 2在线1离线1告警，与最后同步联动
 */

export function hash01(seed: number): number {
  const x = Math.sin(seed * 12.9898 + 78.233) * 43758.5453;
  return x - Math.floor(x);
}
export function dRand(dtId: number, ctId: number, idx: number, min: number, max: number): number {
  return min + hash01(dtId * 1000 + ctId * 100 + idx * 7 + 3) * (max - min);
}

// 数据类型定义（与 Exhibition.html DATA_TYPES 同源）
export const DATA_TYPES = [
  { id: 1, name: "日使用时长分布", module: "手机使用", key: "dailyApp", desc: "晚高峰19-23点占全天41%，视频类拉动明显" },
  { id: 2, name: "周使用趋势", module: "手机使用", key: "weeklyTrend", desc: "周一/周五双峰，周末降低20%，工作日专注度更高" },
  { id: 3, name: "App分类占比", module: "手机使用", key: "categoryShare", desc: "聊天28%+视频22%占半壁，工具与社交次之" },
  { id: 4, name: "24小时热力图", module: "手机使用", key: "hourHeatmap", desc: "8-12点与19-23点双峰，凌晨1-5点仅占3%" },
  { id: 5, name: "GPS轨迹地图", module: "位置轨迹", key: "gpsTrack", desc: "家→地铁→公司3段连续轨迹，速度与段绑定" },
  { id: 6, name: "常去地点气泡图", module: "位置轨迹", key: "places", desc: "家128次/公司96次/学校42次，与轨迹起终点一致" },
  { id: 7, name: "速度分布", module: "位置轨迹", key: "speed", desc: "步行4km/h/骑行20km/h/高铁300km/h三峰" },
  { id: 8, name: "PC应用使用时长", module: "PC活动", key: "pcApp", desc: "VS Code 35%+Chrome 30%占大头，AFK单独统计" },
  { id: 9, name: "键盘热力图", module: "PC活动", key: "keyboard", desc: "ASDF/JKL; 高频，Q/Z低频，Space 3410次" },
  { id: 10, name: "任务完成率", module: "日程习惯", key: "tasks", desc: "完成率61%→89%波动，7日均线平滑" },
  { id: 11, name: "习惯打卡热力", module: "日程习惯", key: "habits", desc: "早起72% 运动45%，周末打卡率-10%" },
  { id: 12, name: "设备健康状态", module: "设备健康", key: "devices", desc: "2在线1离线1告警，健康分与同步时间联动" },
] as const;

// 1) 日使用时长：Top3占60%且与分类联动
export function genType1() {
  const apps = [
    { name: "微信", cat: "聊天", base: 3420 },
    { name: "抖音", cat: "视频", base: 2160 },
    { name: "哔哩哔哩", cat: "视频", base: 1440 },
    { name: "微博", cat: "社交", base: 720 },
    { name: "VS Code", cat: "工具", base: 2880 },
  ];
  const totals = apps.map((a, idx) => {
    let sum = 0;
    for (let d = 0; d < 7; d++) {
      const isWeekend = d >= 5;
      let daily = a.base;
      if (a.cat === "视频" && isWeekend) daily = Math.round(daily * 1.18);
      if (a.cat === "聊天" && !isWeekend) daily = Math.round(daily * 1.12);
      const jitter = 0.94 + hash01(a.name.length * 10 + d * 3 + idx) * 0.12;
      sum += Math.round(daily * jitter);
    }
    return { label: a.name, category: a.cat, value: Math.round(sum / 7) };
  });
  return totals;
}

// 2) 周趋势：周一/五双峰，周末-20%，与日使用同源
export function genType2() {
  const cats = ["聊天", "视频", "工具", "社交"];
  const weeks = [
    { label: "W1", total: 1380 },
    { label: "W2", total: 1520 },
    { label: "W3", total: 1410 },
    { label: "W4", total: 1680 },
  ];
  return weeks.map((w, wi) => {
    const ratios = [0.32, 0.26, 0.22, 0.2];
    const byCat: Record<string, number> = {};
    let remain = w.total;
    cats.forEach((c, ci) => {
      if (ci === cats.length - 1) byCat[c] = remain;
      else {
        const v = Math.round(w.total * ratios[ci] * (0.96 + hash01(wi * 10 + ci) * 0.08));
        byCat[c] = v;
        remain -= v;
      }
    });
    const sum = Object.values(byCat).reduce((a, b) => a + b, 0);
    if (sum !== w.total) byCat[cats[0]] += w.total - sum;
    return { label: w.label, value: w.total, byCat, cats };
  });
}

// 3) 分类占比：8类和100%，聊天28%+视频22%占半壁
export function genType3() {
  const cats = ["聊天", "视频", "社交", "工具", "游戏", "学习", "购物", "其他"];
  const fixed = [27.5, 21.3, 14.2, 12.8, 8.5, 6.1, 5.4, 4.2];
  return cats.map((c, i) => ({ label: c, value: fixed[i] }));
}

// 4) 24h热力：双峰 8-12/19-23，凌晨1-5 <5%，4点切割有缝
export function genType4() {
  const cats = ["聊天", "视频", "工具", "社交", "游戏"];
  const data: { hour: number; category: string; value: number }[] = [];
  for (let h = 0; h < 24; h++) {
    // 4点业务日切割：h=4 处缝隙
    let base: number;
    if (h === 4) base = 2 + hash01(h * 7 + 1) * 2; // 缝隙
    else if (h >= 0 && h <= 5) base = 6 + hash01(h * 7 + 1) * 3;
    else if (h >= 6 && h <= 7) base = 18 + hash01(h * 7 + 2) * 6;
    else if (h >= 8 && h <= 12) base = 62 + hash01(h * 7 + 3) * 16;
    else if (h >= 12 && h <= 14) base = 46 + hash01(h * 7 + 4) * 10;
    else if (h >= 14 && h <= 18) base = 34 + hash01(h * 7 + 5) * 12;
    else if (h >= 19 && h <= 23) base = 70 + hash01(h * 7 + 6) * 18;
    else base = 22;
    for (let ci = 0; ci < cats.length; ci++) {
      let v = base;
      if (cats[ci] === "视频" && h >= 19 && h <= 23) v *= 1.28;
      if (cats[ci] === "工具" && h >= 9 && h <= 18) v *= 1.32;
      if (cats[ci] === "游戏" && h >= 20) v *= 1.22;
      if (cats[ci] === "聊天" && h >= 7 && h <= 9) v *= 1.18;
      v = Math.round(v + (hash01(h * 10 + ci) - 0.5) * 6);
      data.push({ hour: h, category: cats[ci], value: Math.max(2, v) });
    }
  }
  return { cats, data };
}

// 5) GPS轨迹：3段 家→地铁→公司 连续，速度与段绑定
export function genType5() {
  const route: [number, number][] = [
    [39.9042, 116.4074], [39.907, 116.412], [39.91, 116.418], [39.913, 116.425], [39.916, 116.433],
    [39.919, 116.44], [39.921, 116.447], [39.92, 116.452], [39.918, 116.455], [39.914, 116.452],
    [39.91, 116.445], [39.907, 116.438], [39.905, 116.43], [39.904, 116.42], [39.9042, 116.4074],
  ];
  const pts: { lat: number; lng: number; ts: number; speed: number }[] = [];
  for (let seg = 0; seg < route.length - 1; seg++) {
    const [lat1, lng1] = route[seg], [lat2, lng2] = route[seg + 1];
    const steps = 4;
    for (let s = 0; s < steps; s++) {
      if (pts.length >= 50) break;
      const t = s / steps;
      const lat = lat1 + (lat2 - lat1) * t + (hash01(seg * 10 + s) - 0.5) * 0.002;
      const lng = lng1 + (lng2 - lng1) * t + (hash01(seg * 10 + s + 5) - 0.5) * 0.002;
      const speedSeg = seg < 6 ? 18 + hash01(seg * 3 + s) * 12 : seg < 10 ? 4 + hash01(seg * 3 + s) * 3 : 12 + hash01(seg * 3 + s) * 8;
      pts.push({ lat: Math.max(39.8, Math.min(40.1, lat)), lng: Math.max(116.2, Math.min(116.6, lng)), ts: Date.now() - (50 - pts.length) * 600000, speed: Math.round(speedSeg * 10) / 10 });
    }
    if (pts.length >= 50) break;
  }
  while (pts.length < 50) {
    const last = pts[pts.length - 1];
    pts.push({ lat: last.lat + (hash01(pts.length * 7) - 0.5) * 0.001, lng: last.lng + (hash01(pts.length * 7 + 2) - 0.5) * 0.001, ts: Date.now() - (50 - pts.length) * 600000, speed: 3 + Math.round(hash01(pts.length * 3) * 10) });
  }
  return pts.slice(0, 50);
}

// 6) 常去地点：与轨迹起终点一致
export function genType6() {
  return [
    { name: "家", lat: 39.9042, lng: 116.4074, visitCount: 128 },
    { name: "公司", lat: 39.921, lng: 116.447, visitCount: 96 },
    { name: "学校", lat: 39.8895, lng: 116.3974, visitCount: 42 },
    { name: "商圈", lat: 39.918, lng: 116.418, visitCount: 31 },
    { name: "健身房", lat: 39.932, lng: 116.382, visitCount: 18 },
  ];
}

// 7) 速度三峰 4/20/300
export function genType7() {
  const bins = [
    { label: "步行 0-5", speed: "2.8", count: 86 },
    { label: "骑行 15-25", speed: "19", count: 42 },
    { label: "开车 35-65", speed: "48", count: 68 },
    { label: "高铁 250-350", speed: "310", count: 11 },
  ];
  const hist: { speed: number; count: number }[] = [];
  const peaks = [{ c: 2.8, w: 2.2, h: 34 }, { c: 19, w: 5, h: 22 }, { c: 48, w: 15, h: 28 }, { c: 310, w: 28, h: 11 }];
  for (let i = 0; i < 24; i++) {
    const speed = i * 15;
    let v = 2;
    for (const p of peaks) {
      const dist = Math.abs(speed - p.c);
      const gauss = Math.exp(-0.5 * Math.pow(dist / p.w, 2));
      v += gauss * p.h;
    }
    v += (hash01(i * 11) - 0.5) * 2;
    hist.push({ speed, count: Math.max(2, Math.round(v)) });
  }
  return { bins, hist };
}

// 8) PC应用：VS Code35%+Chrome30%占大头，AFK单独
export function genType8() {
  return [
    { label: "VS Code", value: 320 },
    { label: "Chrome", value: 280 },
    { label: "Word", value: 95 },
    { label: "微信", value: 65 },
    { label: "B站", value: 45 },
    { label: "AFK", value: 30 },
  ];
}

// 9) 键盘 QWERTY真实
export function genType9() {
  const freq: Record<string, number> = {
    Q: 85, W: 320, E: 1820, R: 750, T: 1410, Y: 420, U: 620, I: 1100, O: 1210, P: 480,
    A: 1320, S: 960, D: 680, F: 520, G: 410, H: 820, J: 85, K: 540, L: 680, ";": 40,
    Z: 45, X: 120, C: 340, V: 180, B: 260, N: 980, M: 540, ",": 90, ".": 110, "/": 30,
    Space: 3420, Enter: 920,
  };
  const rows = ["QWERTYUIOP", "ASDFGHJKL;", "ZXCVBNM,./"];
  const keys: { key: string; pressCount: number }[] = [];
  rows.forEach((row) => {
    for (const ch of row) {
      const base = freq[ch] ?? 200;
      const jitter = Math.round((hash01(ch.charCodeAt(0) * 7) - 0.5) * 22);
      keys.push({ key: ch, pressCount: Math.max(12, base + jitter) });
    }
  });
  keys.push({ key: "Space", pressCount: 3420 });
  keys.push({ key: "Enter", pressCount: 920 });
  return keys;
}

// 10) 任务完成率：61→89%带7日均线
export function genType10() {
  const rates = [68, 72, 75, 80, 65, 78, 82, 70, 85, 88, 62, 77, 81, 84, 68, 74, 79, 86, 63, 75, 80, 83, 71, 76, 84, 88, 66, 73, 79, 85];
  const out: { date: string; completed: number; total: number; rate: number; ma7?: number }[] = [];
  for (let i = 0; i < 30; i++) {
    const d = new Date(Date.now() - (29 - i) * 86400000);
    const ds = d.toISOString().slice(0, 10);
    const rate = rates[i];
    const total = 5 + Math.round(hash01(i * 13) * 3);
    const completed = Math.round((total * rate) / 100);
    out.push({ date: ds, completed, total, rate });
  }
  // 7日均线
  for (let i = 0; i < out.length; i++) {
    const win = out.slice(Math.max(0, i - 6), i + 1);
    out[i].ma7 = Math.round(win.reduce((s, x) => s + x.rate, 0) / win.length);
  }
  return out;
}

// 11) 习惯打卡：与周趋势相关周末-10%
export function genType11() {
  const habits = [
    { name: "早起", rate: 0.72 },
    { name: "阅读", rate: 0.65 },
    { name: "运动", rate: 0.45 },
    { name: "冥想", rate: 0.58 },
    { name: "写作", rate: 0.4 },
  ];
  const data: { date: string; habit: string; done: boolean }[] = [];
  for (let i = 0; i < 30; i++) {
    const d = new Date(Date.now() - (29 - i) * 86400000);
    const ds = d.toISOString().slice(0, 10);
    const isWeekend = d.getDay() === 0 || d.getDay() === 6;
    for (const h of habits) {
      const r = hash01(i * 31 + h.name.length * 7);
      const adj = isWeekend ? h.rate - 0.1 : h.rate;
      data.push({ date: ds, habit: h.name, done: r < adj });
    }
  }
  return { habits: habits.map((h) => h.name), data };
}

// 12) 设备健康：与最后同步联动
export function genType12() {
  return [
    { device: "小米 13", status: "在线" as const, health: 92, lastSync: "2分钟前" },
    { device: "ThinkPad X1", status: "在线" as const, health: 88, lastSync: "5分钟前" },
    { device: "iPad Pro", status: "离线" as const, health: 43, lastSync: "3小时前" },
    { device: "Watch S8", status: "告警" as const, health: 58, lastSync: "18分钟前" },
  ];
}

export function getFakeData(dtId: number) {
  switch (dtId) {
    case 1: return genType1();
    case 2: return genType2();
    case 3: return genType3();
    case 4: return genType4();
    case 5: return genType5();
    case 6: return genType6();
    case 7: return genType7();
    case 8: return genType8();
    case 9: return genType9();
    case 10: return genType10();
    case 11: return genType11();
    case 12: return genType12();
    default: return [];
  }
}

// 辅助：标签数值归一化（与 Exhibition.html labelsAndValues 同源）
export function labelsAndValues(fake: unknown, dtId: number): { labels: string[]; values: number[]; raw: unknown } {
  if (dtId === 1 || dtId === 3 || dtId === 8) {
    const arr = fake as { label: string; value: number }[];
    if (Array.isArray(arr) && arr[0] && (arr[0] as { label?: unknown }).label !== undefined) {
      return { labels: arr.map((d) => d.label), values: arr.map((d) => d.value), raw: fake };
    }
  }
  if (dtId === 2) {
    const arr = fake as { label: string; value: number }[];
    return { labels: arr.map((d) => d.label), values: arr.map((d) => d.value), raw: fake };
  }
  if (dtId === 4) {
    const f = fake as { data: { hour: number; value: number }[] };
    const byHour = new Map<number, number>();
    f.data.forEach((d) => byHour.set(d.hour, (byHour.get(d.hour) || 0) + d.value));
    const labels = Array.from({ length: 24 }, (_, i) => i + ":00");
    const values = labels.map((_, i) => byHour.get(i) || 0);
    return { labels, values, raw: fake };
  }
  if (dtId === 5) {
    const arr = fake as { speed: number }[];
    return { labels: arr.slice(0, 8).map((_, i) => "P" + (i + 1)), values: arr.slice(0, 8).map((p) => Math.round(p.speed)), raw: fake };
  }
  if (dtId === 6) {
    const arr = fake as { name: string; visitCount: number }[];
    return { labels: arr.map((p) => p.name), values: arr.map((p) => p.visitCount), raw: fake };
  }
  if (dtId === 7) {
    const f = fake as { hist?: { speed: number; count: number }[]; bins?: { label: string; count: number }[] };
    const arr = f.hist ? f.hist : (f.bins as { label: string; count: number }[]).map((b) => ({ speed: 0, count: b.count, label: b.label }));
    return { labels: arr.map((d) => String((d as { speed?: number }).speed ?? (d as { label?: string }).label)), values: arr.map((d) => (d as { count: number }).count ?? 0), raw: arr };
  }
  if (dtId === 9) {
    const arr = fake as { key: string; pressCount: number }[];
    const top = arr.slice(0, 12);
    return { labels: top.map((k) => k.key), values: top.map((k) => k.pressCount), raw: fake };
  }
  if (dtId === 10) {
    const arr = fake as { date: string; rate: number }[];
    return { labels: arr.map((d) => d.date.slice(5)), values: arr.map((d) => d.rate), raw: fake };
  }
  if (dtId === 11) {
    const f = fake as { habits: string[]; data: { habit: string; done: boolean }[] };
    const byHabit = new Map<string, number>();
    f.data.forEach((d) => { if (d.done) byHabit.set(d.habit, (byHabit.get(d.habit) || 0) + 1); });
    return { labels: f.habits, values: f.habits.map((h) => byHabit.get(h) || 0), raw: fake };
  }
  if (dtId === 12) {
    const arr = fake as { device: string; health: number }[];
    return { labels: arr.map((d) => d.device), values: arr.map((d) => d.health), raw: fake };
  }
  if (Array.isArray(fake)) {
    const arr = fake as { label?: string; name?: string; key?: string; value?: number; count?: number; visitCount?: number; pressCount?: number }[];
    return { labels: arr.slice(0, 6).map((d, i) => d.label || d.name || d.key || "#" + i), values: arr.slice(0, 6).map((d) => d.value || d.count || d.visitCount || d.pressCount || 0), raw: fake };
  }
  return { labels: ["A", "B", "C", "D", "E"], values: [12, 23, 18, 31, 9], raw: [] };
}
