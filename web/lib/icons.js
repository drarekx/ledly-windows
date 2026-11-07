// Line icons (24×24, stroked with currentColor), standing in for the Mac app's SF Symbols.

const paths = {
  home: "M3 10.5 12 3l9 7.5M5 9v11h5v-6h4v6h5V9",
  sparkles: "M12 3l1.8 4.7L18.5 9.5l-4.7 1.8L12 16l-1.8-4.7L5.5 9.5l4.7-1.8zM19 15l.8 2.2L22 18l-2.2.8L19 21l-.8-2.2L16 18l2.2-.8zM5 15l.6 1.4L7 17l-1.4.6L5 19l-.6-1.4L3 17l1.4-.6z",
  layout: "M4 4h3M10 4h4M17 4h3v3M20 10v4M20 17v3h-3M14 20h-4M7 20H4v-3M4 14v-4M4 7V4",
  cpu: "M7 7h10v10H7zM10 10h4v4h-4zM9 3v4M15 3v4M9 17v4M15 17v4M3 9h4M3 15h4M17 9h4M17 15h4",
  image: "M12 3a5 5 0 1 0 0 10 5 5 0 0 0 0-10zM8 10a5 5 0 1 0 4 8M16 10a5 5 0 1 1-4 8",
  network: "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM3 12h18M12 3c2.5 2.6 3.7 5.6 3.7 9s-1.2 6.4-3.7 9c-2.5-2.6-3.7-5.6-3.7-9S9.5 5.6 12 3z",
  sliders: "M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12M20 18h0M14 4v4M8 10v4M16 16v4",
  power: "M12 3v8M7.1 6.2a7 7 0 1 0 9.8 0",
  bulb: "M9 18h6M10 21h4M12 3a6 6 0 0 0-3.5 10.9c.6.5 1 1.2 1 2.1h5c0-.9.4-1.6 1-2.1A6 6 0 0 0 12 3z",
  wand: "M4 20 15 9M14 4l.7 1.8L16.5 6.5l-1.8.7L14 9l-.7-1.8L11.5 6.5l1.8-.7zM19 10l.5 1.3 1.3.5-1.3.5L19 13.6l-.5-1.3-1.3-.5 1.3-.5zM8 3l.4 1.1 1.1.4-1.1.4L8 6l-.4-1.1-1.1-.4 1.1-.4z",
  refresh: "M20 11a8 8 0 1 0-2.3 5.7M20 4v7h-7",
  copy: "M9 9h11v11H9zM5 15H4V4h11v1",
  check: "M5 12.5l4.5 4.5L19 7.5",
  checkCircle: "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM8 12.2l2.8 2.8L16.5 9",
  xCircle: "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM9 9l6 6M15 9l-6 6",
  alert: "M12 4 2.5 20h19zM12 10v4M12 17v.5",
  cable: "M8 3v4M16 3v4M6 7h12v4a6 6 0 0 1-12 0zM12 17v4",
  arrowUpCircle: "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM12 16V8M8.5 11.5 12 8l3.5 3.5",
  download: "M12 4v11M7 10l5 5 5-5M5 20h14",
  chart: "M5 20V12M10 20V6M15 20v-9M20 20V9",
  music: "M9 18V5l11-2v13M9 18a3 3 0 1 1-6 0 3 3 0 0 1 6 0zM20 16a3 3 0 1 1-6 0 3 3 0 0 1 6 0z",
  flame: "M12 3c1 3.5 5 5.5 5 10a5 5 0 0 1-10 0c0-2.5 1.3-3.8 2.5-5 .3 1.6 1 2.6 2 3 0-3-1-5.5.5-8z",
  wave: "M2 12c2.5-4 5-4 7.5 0s5 4 7.5 0 3.5-3 5-1.5M2 17c2.5-4 5-4 7.5 0s5 4 7.5 0 3.5-3 5-1.5",
  rainbow: "M3 18a9 9 0 0 1 18 0M6.5 18a5.5 5.5 0 0 1 11 0M10 18a2 2 0 0 1 4 0",
  rays: "M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6zM12 2v4M12 18v4M2 12h4M18 12h4M4.9 4.9l2.8 2.8M16.3 16.3l2.8 2.8M4.9 19.1l2.8-2.8M16.3 7.7l2.8-2.8",
  cycle: "M20 12a8 8 0 0 1-14 5.3M4 12A8 8 0 0 1 18 6.7M18 3v4h-4M6 21v-4h4",
  atom: "M12 11a1 1 0 1 0 0 2 1 1 0 0 0 0-2zM12 3c4.4 0 8 4 8 9s-3.6 9-8 9-8-4-8-9 3.6-9 8-9zM4.2 7.5c2.2-3.8 7.6-4.5 12-2s6 7.7 3.8 11.5-7.6 4.5-12 2S2 11.3 4.2 7.5z",
  wind: "M3 8h11a3 3 0 1 0-3-3M3 12h16a3 3 0 1 1-3 3M3 16h8",
  candle: "M9 11h6v10H9zM12 11V8M12 3c1.2 1.5 1.5 2.6.8 3.6-.4.6-1.2.6-1.6 0-.7-1 0-2.1.8-3.6z",
  car: "M3 16v-3l2-5h11l3 4h2v4h-2M7 16H3M15 16H9M7 17.5a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3zM17 17.5a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3z",
  beacon: "M7 20h10M8 20v-7a4 4 0 0 1 8 0v7M12 3v3M4.5 6.5l2 2M19.5 6.5l-2 2",
  bolt: "M13 2 4 14h7l-1 8 9-12h-7z",
  sparkle: "M12 3l2 7 7 2-7 2-2 7-2-7-7-2 7-2z",
  blobs: "M7 6a3 3 0 1 0 0 6 3 3 0 0 0 0-6zM17 5a3 3 0 1 0 0 6 3 3 0 0 0 0-6zM12 13a4 4 0 1 0 0 8 4 4 0 0 0 0-8z",
  plasma: "M3 12c3-6 6 6 9 0s6 6 9 0M3 7c3-4 6 4 9 0s6 4 9 0M3 17c3-4 6 4 9 0s6 4 9 0",
  snake: "M4 18c4 0 3-6 7-6s3 6 7 6M17 6a2 2 0 1 0 0 4 2 2 0 0 0 0-4zM4 6c3 0 4 2 7 2",
  brush: "M14 4l6 6-8.5 8.5-6-6zM5.5 12.5 3 21l8.5-2.5",
  tree: "M12 2 6 10h3l-4 6h14l-4-6h3zM12 16v5",
  clock: "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM12 7v5l3.5 2",
  moon: "M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5zM17 3v3M15.5 4.5h3",
  meteor: "M14 10a4 4 0 1 0 0 8 4 4 0 0 0 0-8zM3 3l8.5 8.5M7 3l6.5 6.5M3 7l6.5 6.5",
  radio: "M12 11a1.5 1.5 0 1 0 0 3 1.5 1.5 0 0 0 0-3zM8.5 8.5a5 5 0 0 0 0 7M15.5 8.5a5 5 0 0 1 0 7M5.5 5.5a9 9 0 0 0 0 13M18.5 5.5a9 9 0 0 1 0 13",
  gauge: "M4 17a8 8 0 1 1 16 0M12 17l4-6",
  sun: "M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8zM12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4",
  sunDim: "M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6zM12 5v.5M12 18.5v.5M5 12h.5M18.5 12h.5M7 7l.4.4M16.6 16.6l.4.4M7 17l.4-.4M16.6 7.4l.4-.4",
  bell: "M6 16V11a6 6 0 0 1 12 0v5l2 2H4zM10 20a2 2 0 0 0 4 0",
  undo: "M9 14 4 9l5-5M4 9h10a6 6 0 0 1 0 12h-3",
  arrowRight: "M5 12h14M13 6l6 6-6 6",
  external: "M14 4h6v6M20 4l-9 9M18 14v6H4V6h6",
  coffee: "M4 9h13v5a5 5 0 0 1-5 5H9a5 5 0 0 1-5-5zM17 10h1.5a2.5 2.5 0 0 1 0 5H17M8 3v3M12 3v3",
  monitor: "M3 4h18v12H3zM8 20h8M12 16v4",
  speed: "M4 17a8 8 0 1 1 16 0M12 17l-3-5M7 17h.5M16.5 17h.5",
  minus: "M5 12h14",
  plus: "M12 5v14M5 12h14",
  circleHalf: "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM12 3v18",
  quit: "M10 4H5v16h5M15 8l4 4-4 4M19 12H9",
  doc: "M6 3h8l4 4v14H6zM14 3v4h4M9 12h6M9 16h6",
};

/** <svg> for `name` (unknown names draw nothing). */
export function icon(name, size = 16) {
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("width", size);
  svg.setAttribute("height", size);
  svg.setAttribute("fill", "none");
  svg.setAttribute("stroke", "currentColor");
  svg.setAttribute("stroke-width", "1.8");
  svg.setAttribute("stroke-linecap", "round");
  svg.setAttribute("stroke-linejoin", "round");
  svg.setAttribute("aria-hidden", "true");
  const p = document.createElementNS(ns, "path");
  p.setAttribute("d", paths[name] ?? "");
  svg.append(p);
  return svg;
}
