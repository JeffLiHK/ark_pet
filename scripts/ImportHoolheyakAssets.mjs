import { createHash } from "node:crypto";
import { existsSync } from "node:fs";
import { copyFile, mkdir, mkdtemp, readFile, realpath, rename, rm, rmdir, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../", import.meta.url));
const localData = path.join(root, "local-data");
const tools = path.join(localData, "tools");
const models = path.join(root, "Ark-Models", "models");
const portraits = path.join(root, "ArknightsGameResource", "portrait");
const canvasSize = 320;
const fps = 60;
const animations = { idle: "Relax", move: "Move", sit: "Sit", sleep: "Sleep", touch: "Interact" };
const { id, displayName: character, defaultOutfitId, outfits } = JSON.parse(
    await readFile(new URL("./HoolheyakSource.json", import.meta.url), "utf8"));

const require = createRequire(path.join(tools, "package.json"));
if (!existsSync(path.join(tools, "node_modules", "playwright-core"))) {
    throw new Error("Install the export dependency with npm install --prefix local-data/tools --no-save --no-package-lock playwright-core@1.56.1");
}
const { chromium } = require("playwright-core");
const canonicalData = await realpath(localData);
if (path.relative(await realpath(root), canonicalData) !== "local-data") {
    throw new Error("The local-data folder must be inside the project directory.");
}

const runtimeFile = path.join(tools, "spine-webgl-3.8.js");
const runtimeHash = "4b108bad314c152bf0ff7a7f56fce5c98073f05a2bfa53e880d6c24b6f7c3aa5";
if (!existsSync(runtimeFile)) {
    const response = await fetch("https://raw.githubusercontent.com/EsotericSoftware/spine-runtimes/8b4844bd4b193ba9e54487ed397a777993cbad56/spine-ts/build/spine-webgl.js",
        { signal: AbortSignal.timeout(30_000) });
    if (!response.ok) {
        throw new Error(`Spine Runtime download failed: HTTP ${response.status}`);
    }
    const runtime = Buffer.from(await response.arrayBuffer());
    checkRuntime(runtime);
    await writeFile(runtimeFile, runtime);
}
checkRuntime(await readFile(runtimeFile));

function checkRuntime(runtime) {
    if (createHash("sha256").update(runtime).digest("hex") !== runtimeHash) {
        throw new Error("Spine Runtime does not match the pinned 3.8 build.");
    }
}

const browser = await chromium.launch({
    channel: "msedge",
    headless: true,
    args: ["--use-angle=swiftshader", "--enable-unsafe-swiftshader"],
});
const stage = await mkdtemp(path.join(canonicalData, ".hoolheyak-"));
if (path.dirname(await realpath(stage)) !== canonicalData) {
    throw new Error(`Unexpected staging path: ${stage}`);
}
try {
    const page = await browser.newPage();
    await page.addScriptTag({ path: runtimeFile });
    await page.addScriptTag({ path: path.join(root, "src/ArkPet/Media/SpineFrameRenderer.js") });
    await page.evaluate(args => window.startRenderer(args), { size: canvasSize, fps, animations });

    const modelStage = path.join(stage, "models");
    const assetStage = path.join(stage, "assets");
    const manifestOutfits = new Map();
    const clipsByOutfit = new Map();
    for (const outfit of outfits.filter(outfit => outfit.model)) {
        const directory = path.join(models, outfit.model.toLowerCase());
        const model = path.join(directory, "build_char_" + outfit.model);
        const atlas = await readFile(model + ".atlas", "utf8");
        const texture = atlas.trimStart().split(/\r?\n/, 1)[0];
        if (path.basename(texture) !== texture || !texture.endsWith(".png")) {
            throw new Error(`Invalid texture path for ${outfit.name}: ${texture}`);
        }
        const texturePath = path.join(directory, texture);
        const clips = await page.evaluate(args => window.renderer.load(args), {
            id: outfit.id,
            atlas,
            texture,
            png: await readFile(texturePath, "base64"),
            binary: await readFile(model + ".skel", "base64"),
        });
        if (Object.values(clips).reduce((total, frames) => total + frames, 0) > 4096) {
            throw new Error(`${outfit.name} exceeds the frame limit for one outfit.`);
        }
        clipsByOutfit.set(outfit.id, clips);

        const target = path.join(modelStage, outfit.name);
        await mkdir(target, { recursive: true });
        for (const file of [model + ".atlas", model + ".skel", texturePath]) {
            await copyFile(file, path.join(target, path.basename(file)));
        }
    }

    const canvas = await page.evaluate(() => window.renderer.frame());
    console.log(`Canvas ${canvas.width} × ${canvas.height}, anchor ${canvas.anchorX}, ${canvas.anchorY}`);
    for (const outfit of outfits) {
        const directory = path.join(assetStage, outfit.name);
        await mkdir(directory, { recursive: true });
        await copyFile(path.join(portraits, outfit.portrait), path.join(directory, "portrait.png"));
        const art = outfit.sharedModel
            ? manifestOutfits.get(outfit.sharedModel)
            : await exportOutfit(page, directory, outfit, clipsByOutfit.get(outfit.id));
        manifestOutfits.set(outfit.id, {
            ...art,
            id: outfit.id,
            displayName: outfit.name,
            portrait: `${outfit.name}/portrait.png`,
        });
    }

    const manifest = { schemaVersion: 1, id, displayName: character, defaultOutfitId, canvas, outfits: [...manifestOutfits.values()] };
    await writeFile(path.join(assetStage, "character.json"), JSON.stringify(manifest, null, 2) + "\n");
    await installPack([
        { source: modelStage, destination: path.join(localData, "models", character) },
        { source: assetStage, destination: path.join(localData, "assets", character) },
    ]);
    console.log(`Installed ${manifestOutfits.size} wardrobe entries for ${character}.`);
} finally {
    await browser.close();
    await rm(stage, { recursive: true, force: true });
}

async function exportOutfit(page, directory, outfit, clips) {
    const result = { preview: `${outfit.name}/preview.png` };
    for (const [clip, frameCount] of Object.entries(clips)) {
        const clipDirectory = path.join(directory, clip);
        await mkdir(clipDirectory);
        result[clip] = { directory: `${outfit.name}/${clip}`, fps, frameCount };
        console.log(`${outfit.name} / ${clip}: ${frameCount} frames`);
        const batchSize = 20;
        for (let start = 0; start < frameCount; start += batchSize) {
            const frames = await page.evaluate(args => window.renderer.render(args), {
                id: outfit.id, clip, start, end: Math.min(start + batchSize, frameCount),
            });
            await Promise.all(frames.map((png, offset) =>
                writeFile(path.join(clipDirectory, `${String(start + offset).padStart(4, "0")}.png`), png, "base64")));
        }
    }
    await copyFile(path.join(directory, "idle", "0000.png"), path.join(directory, "preview.png"));
    return result;
}

async function installPack(entries) {
    const lock = path.join(canonicalData, ".hoolheyak-install");
    await mkdir(lock);
    const installed = [];
    const saved = [];
    let backups;
    try {
        backups = await mkdtemp(path.join(canonicalData, ".hoolheyak-backup-"));
        for (const { source, destination } of entries) {
            const parent = path.dirname(destination);
            await mkdir(parent, { recursive: true });
            if (path.relative(canonicalData, await realpath(parent)) !== path.basename(parent)) {
                throw new Error(`Unexpected installation directory: ${parent}`);
            }
            if (existsSync(destination)) {
                const backup = path.join(backups, path.basename(parent));
                await rename(destination, backup);
                saved.push({ backup, destination });
            }
            await rename(source, destination);
            installed.push({ source, destination });
        }
    } catch (error) {
        for (const { source, destination } of installed.reverse()) {
            await rename(destination, source);
        }
        for (const { backup, destination } of saved.reverse()) {
            await rename(backup, destination);
        }
        throw error;
    } finally {
        if (backups && saved.length === 0) {
            await rmdir(backups);
        }
        await rmdir(lock);
    }
    if (saved.length > 0) {
        console.log(`Previous model and asset folders were backed up in ${backups}.`);
    }
}
