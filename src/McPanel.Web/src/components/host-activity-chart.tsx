import { useMemo } from "react"
import { Chart, ChartPalette, type KumoChartOption } from "@cloudflare/kumo"
import * as echarts from "echarts/core"
import { LineChart } from "echarts/charts"
import { AriaComponent, GridComponent, LegendComponent, TooltipComponent } from "echarts/components"
import { SVGRenderer } from "echarts/renderers"
import type { HostStatusDto } from "@/lib/contracts"
import { useTheme } from "@/components/theme-provider"

echarts.use([LineChart, GridComponent, TooltipComponent, LegendComponent, AriaComponent, SVGRenderer])

const percent = (value: unknown) => `${Number(value).toFixed(1)}%`
const windowMs = 15 * 60_000

export function HostActivityChart({ samples }: { samples: HostStatusDto["samples"] }) {
  const dark = useTheme().resolvedTheme === "dark"
  const options = useMemo<KumoChartOption>(() => {
    const textColor = ChartPalette.text("primary", dark)
    const series = [
      { name: "CPU", key: "cpu" as const, color: ChartPalette.categorical(0, dark) },
      { name: "Memory", key: "memory" as const, color: ChartPalette.categorical(1, dark) },
    ]
    // Pin the axis to the full window so a freshly started panel does not stretch a few samples edge to edge.
    const end = samples.length ? new Date(samples[samples.length - 1].time).getTime() : undefined
    return {
      backgroundColor: "transparent",
      // Data refreshes every few seconds; re-running the entry animation each time reads as flicker.
      animation: false,
      aria: { enabled: true, label: { description: "Host CPU and memory usage over the last 15 minutes." } },
      grid: { left: 40, right: 16, top: 36, bottom: 24 },
      legend: { top: 0, left: 0, icon: "roundRect", itemWidth: 10, itemHeight: 10, itemGap: 16, textStyle: { color: textColor, fontSize: 12 } },
      tooltip: { trigger: "axis", valueFormatter: percent, axisPointer: { type: "line", lineStyle: { color: textColor, opacity: 0.4 } } },
      xAxis: {
        type: "time",
        min: end === undefined ? undefined : end - windowMs,
        max: end,
        axisLine: { show: false },
        axisTick: { show: false },
        splitLine: { show: false },
        axisLabel: { color: textColor, fontSize: 11, hideOverlap: true, formatter: "{HH}:{mm}" },
      },
      yAxis: {
        type: "value",
        min: 0,
        max: 100,
        interval: 25,
        axisLabel: { color: textColor, fontSize: 11, formatter: "{value}%" },
        splitLine: { lineStyle: { type: "dashed", color: textColor, opacity: 0.2 } },
      },
      series: series.map((item) => ({
        name: item.name,
        type: "line",
        showSymbol: false,
        smooth: 0.25,
        lineStyle: { width: 2 },
        color: item.color,
        areaStyle: {
          color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
            { offset: 0, color: `${item.color}55` },
            { offset: 1, color: `${item.color}00` },
          ]),
        },
        data: samples.map((sample) => [new Date(sample.time).getTime(), sample[item.key]]),
      })),
    }
  }, [dark, samples])

  return <Chart echarts={echarts} options={options} isDarkMode={dark} height={260} />
}
