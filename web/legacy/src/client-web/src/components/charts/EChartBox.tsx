import { useEffect, useRef } from 'react';
import { echarts, type EChartsOption } from '../../lib/echarts';

export interface EChartBoxProps {
  /** ECharts option（option 构建纯函数输出，变更时 notMerge 重设） */
  option: EChartsOption;
  /** 容器高度（px，默认 240）。容器必须显式高度，canvas 才能正确布局 */
  height?: number;
  className?: string;
  /** 无障碍标签，渲染为 role="img" 容器的 aria-label */
  ariaLabel?: string;
  /** 事件绑定：键为 ECharts 事件名（如 'click'），变更时先解绑旧事件再绑定新事件 */
  onEvents?: Record<string, (params: unknown) => void>;
}

/**
 * ECharts 薄封装：init 一次 + ResizeObserver 自适应 + option 变更 setOption(notMerge) + 卸载 dispose。
 * SSR 安全：静态渲染不跑 useEffect，只输出带 role="img" 的占位容器。
 */
export default function EChartBox({ option, height = 240, className, ariaLabel, onEvents }: EChartBoxProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);
  const onEventsRef = useRef(onEvents);
  onEventsRef.current = onEvents;

  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    try {
      const chart = echarts.init(el);
      chartRef.current = chart;
      let resizeObserver: ResizeObserver | null = null;
      if (typeof ResizeObserver !== 'undefined') {
        resizeObserver = new ResizeObserver(() => {
          try {
            chart.resize();
          } catch (error) {
            console.warn('[EChartBox] resize failed', error);
          }
        });
        resizeObserver.observe(el);
      }
      return () => {
        resizeObserver?.disconnect();
        try {
          chart.dispose();
        } catch (error) {
          console.warn('[EChartBox] dispose failed', error);
        }
        chartRef.current = null;
      };
    } catch (error) {
      console.warn('[EChartBox] init failed', error);
      return undefined;
    }
  }, []);

  useEffect(() => {
    const chart = chartRef.current;
    if (!chart) return;
    try {
      chart.setOption(option, { notMerge: true });
    } catch (error) {
      console.warn('[EChartBox] setOption failed', error);
    }
  }, [option]);

  useEffect(() => {
    const chart = chartRef.current;
    if (!chart) return;
    chart.off();
    const events = onEventsRef.current;
    if (events) {
      for (const [name, handler] of Object.entries(events)) {
        chart.on(name, handler);
      }
    }
  }, [onEvents]);

  return (
    <div
      ref={containerRef}
      role="img"
      aria-label={ariaLabel}
      className={className}
      style={{ height }}
    />
  );
}
