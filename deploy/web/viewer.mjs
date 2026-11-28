import { compatible, legendItems, POLL_MS, position, rotation, visibleAppearances } from "./scene.mjs";

const canvas = document.querySelector("#world");
const context = canvas.getContext("2d");
const connection = document.querySelector("#connection");
const label = document.querySelector("#connection-label");
const empty = document.querySelector("#empty");
const age = document.querySelector("#age");
const count = document.querySelector("#count");
const legend = document.querySelector("#legend");
const last = document.querySelector("#last-observation");
const numbers = new Intl.NumberFormat("ru");
const time = new Intl.DateTimeFormat("ru", { timeStyle: "medium" });
const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
const sprites = new Map();
const ground = document.createElement("canvas");
const groundContext = ground.getContext("2d");
let frame;
let previous = new Map();
let started = 0;
let duration = POLL_MS;
let failures = 0;
let scheduled = 0;
let pending = false;
let surface = { width: 0, height: 0 };
let groundSize = "";
let legendKey = "";

function updateLegend(entities) {
  const entries = legendItems(entities);
  const key = JSON.stringify(entries);
  if (legendKey === key) return;
  legendKey = key;
  legend.replaceChildren(...entries.map(([name, color]) => {
    const entry = document.createElement("li");
    const dot = document.createElement("span");
    dot.className = "mark";
    dot.setAttribute("aria-hidden", "true");
    dot.style.backgroundColor = color;
    entry.append(dot, document.createTextNode(name));
    return entry;
  }));
}

function connectionStatus(state) {
  if (connection.dataset.state !== state) {
    connection.dataset.state = state;
    label.textContent = { connecting: "Подключение", live: "В эфире", offline: "Связь прервалась" }[state];
  }
  last.hidden = !frame || state !== "offline";
  if (!last.hidden) last.textContent = "Последний кадр " + time.format(new Date(frame.observedAt));
  if (!frame && state === "offline") empty.textContent = "Мир временно недоступен";
}

async function poll() {
  if (document.hidden || pending) return;
  pending = true;
  const requestTime = performance.now();
  try {
    const response = await fetch("/api/scene", { cache: "no-store", signal: AbortSignal.timeout(4000) });
    if (!response.ok) throw new Error("Scene unavailable");
    const next = await response.json();
    if (next.schemaVersion !== 2 || next.status !== "running") throw new Error("Unsupported scene");
    if (!frame || frame.epoch !== next.epoch || frame.sessionId !== next.sessionId || frame.sequence !== next.sequence) {
      const same = connection.dataset.state === "live" && compatible(frame, next);
      previous = same ? new Map(frame.entities.map(entity => [entity.id, entity])) : new Map();
      duration = same ? Math.max(POLL_MS, Math.min(500, new Date(next.observedAt) - new Date(frame.observedAt))) : POLL_MS;
      frame = next;
      started = performance.now();
      age.textContent = numbers.format(frame.age);
      count.textContent = numbers.format(frame.entities.length);
      updateLegend(frame.entities);
      empty.hidden = true;
    }
    failures = 0;
    connectionStatus("live");
  } catch {
    failures += 1;
    connectionStatus("offline");
  } finally {
    pending = false;
    if (!document.hidden) {
      const delay = failures ? Math.min(8000, 1000 * 2 ** (failures - 1)) : Math.max(0, POLL_MS - (performance.now() - requestTime));
      scheduled = setTimeout(poll, delay);
    }
  }
}

function spriteFor(url) {
  if (!url || !/^\/api\/scene\/sprites\/[a-f0-9]{64}\.(png|webp)$/.test(url)) return null;
  let entry = sprites.get(url);
  if (!entry || (entry.failed && performance.now() > entry.retry)) {
    const image = new Image();
    entry = { image, failed: false, retry: 0 };
    image.addEventListener("error", () => { entry.failed = true; entry.retry = performance.now() + 4000; });
    image.src = url;
    if (sprites.size >= 128) sprites.delete(sprites.keys().next().value);
    sprites.set(url, entry);
  }
  return entry.image.complete && entry.image.naturalWidth ? entry.image : null;
}

function paintGround(width, height) {
  const size = width + "/" + height;
  if (size === groundSize) return;
  groundSize = size;
  ground.width = Math.ceil(width);
  ground.height = Math.ceil(height);
  const shade = groundContext.createLinearGradient(0, 0, width, height);
  shade.addColorStop(0, "#1c352a");
  shade.addColorStop(.57, "#162b25");
  shade.addColorStop(1, "#11261f");
  groundContext.fillStyle = shade;
  groundContext.fillRect(0, 0, width, height);
  // Fixed texture by coordinate so the terrain stays steady between observations.
  for (let i = 0; i < 1100; i += 1) {
    const x = ((i * 7381) % 1867) / 1867 * width;
    const y = ((i * 9377) % 2671) / 2671 * height;
    groundContext.fillStyle = i % 3 ? "#cbe3a60c" : "#030c0930";
    groundContext.fillRect(x, y, i % 5 ? 1 : 2, 1);
  }
  for (let i = 0; i < 165; i += 1) {
    const x = ((i * 1543) % 997) / 997 * width;
    const y = ((i * 1913) % 1327) / 1327 * height;
    groundContext.strokeStyle = i % 3 ? "#86a77b20" : "#b9c98a26";
    groundContext.beginPath();
    groundContext.moveTo(x, y);
    groundContext.quadraticCurveTo(x + 2, y - 5, x + (i % 7) - 3, y - 6);
    groundContext.stroke();
  }
}

function marker(color, radius) {
  context.fillStyle = "#07130c91";
  context.beginPath();
  context.ellipse(1, 2.5, radius + 1, radius * .7, 0, 0, Math.PI * 2);
  context.fill();
  context.fillStyle = color;
  context.beginPath();
  context.arc(0, 0, radius, 0, Math.PI * 2);
  context.fill();
}

function radialGlow(color, radius) {
  const glow = context.createRadialGradient(0, 0, 0, 0, 0, radius);
  glow.addColorStop(0, color + "80");
  glow.addColorStop(.23, color + "44");
  glow.addColorStop(.62, color + "21");
  glow.addColorStop(1, color + "00");
  context.fillStyle = glow;
  context.fillRect(-radius, -radius, radius * 2, radius * 2);
}

function draw(now) {
  requestAnimationFrame(draw);
  if (!surface.width || !surface.height) return;
  const { width, height } = surface;
  context.clearRect(0, 0, width, height);
  if (!frame) return;
  const unit = Math.min(width / frame.width, height / frame.height);
  const mapWidth = frame.width * unit;
  const mapHeight = frame.height * unit;
  const layoutScale = Math.min(mapWidth, mapHeight) / 700;
  const markerScale = Math.max(.65, layoutScale);
  const progress = connection.dataset.state !== "live" || reducedMotion.matches ? 1 : Math.min(1, (now - started) / duration);
  context.save();
  context.translate((width - mapWidth) / 2, (height - mapHeight) / 2);
  context.beginPath();
  context.rect(0, 0, mapWidth, mapHeight);
  context.clip();
  paintGround(mapWidth, mapHeight);
  context.drawImage(ground, 0, 0, mapWidth, mapHeight);
  const renders = [];
  for (const entity of frame.entities) {
    const before = previous.get(entity.id);
    const current = position(entity, before, progress, Math.min(frame.width, frame.height) / 4);
    const x = current.x * unit;
    const y = current.y * unit;
    for (const appearance of visibleAppearances(entity)) {
      const oldRotation = current === entity ? undefined : before?.appearance.find(item => item.key === appearance.key)?.rotation;
      renders.push({ appearance, x, y, angle: rotation(oldRotation, appearance.rotation, progress) });
    }
  }
  for (const { appearance, x, y, angle } of renders.sort((a, b) => a.appearance.layer - b.appearance.layer)) {
    context.save();
    context.translate(x, y);
    context.rotate(angle);
    context.globalAlpha = appearance.alpha;
    const sprite = spriteFor(appearance.sprite);
    const size = appearance.size == null ? null : appearance.size * unit * appearance.scale;
    if (appearance.primitive === "glow") {
      radialGlow(appearance.color, Math.max(1, Math.min((size ?? 16) / 2, Math.max(mapWidth, mapHeight))));
    } else if (sprite) {
      const ratio = sprite.naturalWidth / sprite.naturalHeight;
      const widest = Math.min(size ?? Math.max(sprite.naturalWidth, sprite.naturalHeight) * appearance.scale * layoutScale,
        Math.min(mapWidth, mapHeight) * .7);
      const spriteWidth = ratio >= 1 ? widest : widest * ratio;
      const spriteHeight = ratio >= 1 ? widest / ratio : widest;
      context.drawImage(sprite, -spriteWidth / 2, -spriteHeight / 2, spriteWidth, spriteHeight);
    } else {
      marker(appearance.color, Math.min(28, Math.max(1.5, (size ?? 9 * markerScale * Math.min(appearance.sprite ? 1 : appearance.scale, 4)) / 2)));
    }
    context.restore();
  }
  context.restore();
}

new ResizeObserver(([entry]) => {
  surface = { width: entry.contentRect.width, height: entry.contentRect.height };
  const pixels = Math.min(devicePixelRatio || 1, 2);
  canvas.width = Math.round(surface.width * pixels);
  canvas.height = Math.round(surface.height * pixels);
  context.setTransform(pixels, 0, 0, pixels, 0, 0);
}).observe(canvas);
document.addEventListener("visibilitychange", () => {
  clearTimeout(scheduled);
  if (!document.hidden) poll();
});
requestAnimationFrame(draw);
poll();
