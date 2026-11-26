window.startRenderer = function startRenderer({ size, fps, animations }) {
    const spine = window.spine;
    const canvas = document.createElement("canvas");
    canvas.width = canvas.height = size;
    const gl = canvas.getContext("webgl", { alpha: true, antialias: true, preserveDrawingBuffer: true });
    if (!gl) return false;

    const scene = new spine.webgl.SceneRenderer(canvas, gl);
    const models = new Map();
    const bounds = { left: 0, right: 0, top: 0, bottom: 0 };
    const offset = new spine.Vector2();
    const extent = new spine.Vector2();
    const vertices = [];

    function pose(skeleton, animation, index) {
        skeleton.setToSetupPose();
        animation.apply(skeleton, 0, index / fps, false, null, 1, spine.MixBlend.replace, spine.MixDirection.mixIn);
        skeleton.updateWorldTransform();
    }

    window.renderer = {
        async load({ id, atlas, texture, png, skeletonData }) {
            if (gl.isContextLost()) return null;
            const bytes = Uint8Array.from(atob(png), char => char.charCodeAt(0));
            // Decode without waiting for a visible document or multiplying the texture's alpha again.
            const image = await createImageBitmap(new Blob([bytes], { type: "image/png" }), { premultiplyAlpha: "none" });
            const maxTextureSize = gl.getParameter(gl.MAX_TEXTURE_SIZE);
            if (gl.isContextLost() || image.width > maxTextureSize || image.height > maxTextureSize) {
                image.close();
                return null;
            }

            const textureAtlas = new spine.TextureAtlas(atlas, file => {
                if (file !== texture) throw new Error(`Unexpected atlas page ${file}`);
                // Ark-Models PNGs already contain premultiplied alpha.
                return new spine.webgl.GLTexture(scene.context, image);
            });
            if (gl.getError() !== gl.NO_ERROR) {
                image.close();
                return null;
            }

            const attachmentLoader = new spine.AtlasAttachmentLoader(textureAtlas);
            const skeletonBytes = Uint8Array.from(atob(skeletonData), char => char.charCodeAt(0));
            // Some source packs use the .skel filename for Spine JSON exports.
            const prefix = new TextDecoder().decode(skeletonBytes.subarray(0, 64));
            const data = /^\s*\{\s*"/.test(prefix)
                ? new spine.SkeletonJson(attachmentLoader).readSkeletonData(new TextDecoder().decode(skeletonBytes))
                : new spine.SkeletonBinary(attachmentLoader).readSkeletonData(skeletonBytes);
            const skeleton = new spine.Skeleton(data);
            const clips = {};
            const counts = {};
            const modelBounds = { left: 0, right: 0, top: 0, bottom: 0 };
            let total = 0;
            for (const [key, name] of Object.entries(animations)) {
                const animation = data.findAnimation(name);
                if (!animation) {
                    throw new Error(`${id} has no ${name} animation. Found: ${data.animations.map(item => item.name).join(", ")}`);
                }
                clips[key] = animation;
                const count = Math.ceil(animation.duration * fps - 0.0001);
                total += count;
                if (count < 1 || total > 4096) throw new Error(`Invalid duration for ${id}/${name}`);
                counts[key] = count;
                for (let index = 0; index < count; index++) {
                    pose(skeleton, animation, index);
                    skeleton.getBounds(offset, extent, vertices);
                    if (!Number.isFinite(offset.x + offset.y + extent.x + extent.y)) continue;
                    modelBounds.left = Math.min(modelBounds.left, offset.x);
                    modelBounds.right = Math.max(modelBounds.right, offset.x + extent.x);
                    modelBounds.bottom = Math.min(modelBounds.bottom, offset.y);
                    modelBounds.top = Math.max(modelBounds.top, offset.y + extent.y);
                }
            }
            bounds.left = Math.min(bounds.left, modelBounds.left);
            bounds.right = Math.max(bounds.right, modelBounds.right);
            bounds.bottom = Math.min(bounds.bottom, modelBounds.bottom);
            bounds.top = Math.max(bounds.top, modelBounds.top);
            models.set(id, { skeleton, clips });
            return gl.isContextLost() ? null : counts;
        },
        frame() {
            if (gl.isContextLost()) return null;
            // Give every animation and outfit the same camera and model origin.
            const padding = 12;
            const unitsPerPixel = Math.max(bounds.right - bounds.left, bounds.top - bounds.bottom) / (size - padding * 2);
            if (!(unitsPerPixel > 0)) throw new Error("No visible skeleton bounds were found.");
            const x = (bounds.left + bounds.right) / 2;
            const y = (bounds.top + bounds.bottom) / 2;
            scene.camera.position.set(x, y, 0);
            scene.camera.setViewport(size * unitsPerPixel, size * unitsPerPixel);
            gl.viewport(0, 0, size, size);
            return {
                width: size, height: size,
                anchorX: Math.round(size / 2 - x / unitsPerPixel),
                anchorY: Math.round(size / 2 + y / unitsPerPixel),
            };
        },
        render({ id, clip, start, end }) {
            if (gl.isContextLost()) return null;
            const { skeleton, clips } = models.get(id);
            const frames = [];
            for (let index = start; index < end; index++) {
                pose(skeleton, clips[clip], index);
                gl.clearColor(0, 0, 0, 0);
                gl.clear(gl.COLOR_BUFFER_BIT);
                scene.begin();
                scene.drawSkeleton(skeleton, true);
                scene.end();
                frames.push(canvas.toDataURL("image/png").split(",", 2)[1]);
            }
            return gl.isContextLost() || gl.getError() !== gl.NO_ERROR ? null : frames;
        },
    };
    return true;
};
