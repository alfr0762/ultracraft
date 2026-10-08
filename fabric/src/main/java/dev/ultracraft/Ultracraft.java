package dev.ultracraft;

import com.mojang.blaze3d.platform.InputConstants;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import net.fabricmc.api.ClientModInitializer;
import dev.ultracraft.mixin.AbstractArrowAccessor;
import java.util.UUID;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientChunkEvents;
import net.minecraft.world.entity.projectile.FishingHook;
import net.minecraft.world.entity.projectile.Projectile;
import net.minecraft.world.entity.projectile.arrow.AbstractArrow;
import net.minecraft.world.phys.EntityHitResult;
import net.minecraft.world.phys.HitResult;
import net.minecraft.core.Holder;
import net.minecraft.core.particles.ExplosionParticleInfo;
import net.minecraft.core.particles.ParticleTypes;
import net.minecraft.sounds.SoundEvent;
import net.minecraft.sounds.SoundEvents;
import net.minecraft.util.random.WeightedList;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.level.Explosion;
import net.minecraft.world.level.ExplosionDamageCalculator;
import net.minecraft.world.level.Level;
import net.minecraft.core.Direction;
import net.minecraft.world.level.block.BaseFireBlock;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientTickEvents;
import net.fabricmc.fabric.api.client.keybinding.v1.KeyBindingHelper;
import net.fabricmc.fabric.api.client.rendering.v1.EntityRendererRegistry;
import net.minecraft.client.renderer.entity.NoopRenderer;
import net.minecraft.client.renderer.blockentity.BlockEntityRenderers;
import net.minecraft.world.entity.monster.Enemy;
import net.fabricmc.fabric.api.client.rendering.v1.hud.HudElementRegistry;
import net.fabricmc.fabric.api.client.rendering.v1.hud.VanillaHudElements;
import net.minecraft.client.KeyMapping;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.GuiGraphics;
import net.minecraft.client.multiplayer.ClientLevel;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.client.renderer.RenderPipelines;
import net.minecraft.core.BlockPos;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.resources.Identifier;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.Vec3;
import net.minecraft.world.phys.shapes.VoxelShape;
import org.lwjgl.glfw.GLFW;

/**
 * Ultracraft: real ULTRAKILL inside real Minecraft.
 *
 * ULTRAKILL (with the UltraBridge BepInEx plugin) runs alongside. While "V1 mode" is on, V1 is
 * ULTRAKILL's own player: we forward keyboard and mouse to it, put Minecraft's camera where V1's
 * camera is, draw ULTRAKILL's frame (guns, arm, HUD, effects) over the world, give ULTRAKILL the
 * blocks and mobs around us as colliders and enemies, and turn its hits into Minecraft damage.
 */
public final class Ultracraft implements ClientModInitializer {
	public static volatile boolean active;
	/** An ULTRAKILL menu (spawn menu, alter menu) is open: Minecraft's cursor is free and points at it. */
	public static volatile boolean uiMode;
	/**
	 * Minecraft hands (the hands key, V): V1 puts the guns away and uses items like Steve (mine, place, eat, bows,
	 * hotbar, inventory, drop), while moving, punching and whiplashing stay ULTRAKILL's.
	 */
	public static volatile boolean hands;
	/** V1 stands at a shop's screen: clicks go to ULTRAKILL's shop, whichever hands are out, and Minecraft's hand hides. */
	public static volatile boolean shopTouch;
	/** V1 is close enough to a shop that its screen is on: ULTRAKILL draws at full size, so the screen's text is sharp. */
	public static volatile boolean shopNear;
	/** Wheel notches waiting to go to ULTRAKILL. */
	public static double wheel;

	/** Implemented on every Projectile by ProjectileMixin: apply a hit ULTRAKILL decided on. */
	public interface HitApplier {
		void ultracraft$applyHit(HitResult hit);
	}

	public static boolean isV1(Entity e) {
		return UcNet.isV1(e);
	}
	private static boolean originSent;
	/** Where (and in which dimension) ULTRAKILL's world origin is. */
	private static net.minecraft.world.phys.Vec3 originAt;
	private static net.minecraft.resources.ResourceKey<net.minecraft.world.level.Level> originDim;
	/** A teleport further than this from the origin moves the origin along: ULTRAKILL's floats lose precision far
	 * from its own origin (its HUD text and guns jitter and break up thousands of blocks out). */
	private static final double REBASE = 1024.0;
	private static BlockPos lastExport;
	private static int tick;
	private static int lastW, lastH;
	public static double mouseDx, mouseDy;
	private static double lastCursorX, lastCursorY;
	private static boolean haveCursor;
	/** Ultracraft's own keys (V1/Steve, hands): never given up to a rebind. */
	public static boolean isOwnKey(KeyMapping k) {
		return k == toggle || k == handsKey;
	}

	/**
	 * Steve is playing after having been V1, with ULTRAKILL still running: its enemies stay, drawn from Steve's camera
	 * (bridge SteveView.cs).
	 */
	public static boolean steveView;
	/** Steve's field of view as Minecraft has it (sent to ULTRAKILL with the camera). */
	public static float steveFov = 70f;
	private static boolean everActive;
	private static int fpsCheck, fpsSent;

	private static KeyMapping toggle;
	private static KeyMapping handsKey;
	private static boolean frozen;
	private static long frozenAt;
	private static boolean pauseSent;
	private static LocalPlayer lastPlayer;
	private static boolean wasDead;
	private static boolean autoPending = true;
	private static String lastBlocks;
	private static boolean playingSent;
	private static long lastBlocksAt;

	/** GLFW keys ULTRAKILL can use. */
	private static final int[] KEYS;

	static {
		List<Integer> k = new ArrayList<>();
		for (int c = GLFW.GLFW_KEY_A; c <= GLFW.GLFW_KEY_Z; c++) k.add(c);
		for (int c = GLFW.GLFW_KEY_0; c <= GLFW.GLFW_KEY_9; c++) k.add(c);
		k.add(GLFW.GLFW_KEY_SPACE);
		k.add(GLFW.GLFW_KEY_LEFT_SHIFT);
		k.add(GLFW.GLFW_KEY_RIGHT_SHIFT);
		k.add(GLFW.GLFW_KEY_LEFT_CONTROL);
		k.add(GLFW.GLFW_KEY_RIGHT_CONTROL);
		k.add(GLFW.GLFW_KEY_LEFT_ALT);
		k.add(GLFW.GLFW_KEY_TAB);
		// keys only a rebind would use
		for (int c : new int[] {GLFW.GLFW_KEY_RIGHT_ALT, GLFW.GLFW_KEY_CAPS_LOCK, GLFW.GLFW_KEY_LEFT, GLFW.GLFW_KEY_RIGHT, GLFW.GLFW_KEY_UP, GLFW.GLFW_KEY_DOWN,
			GLFW.GLFW_KEY_COMMA, GLFW.GLFW_KEY_PERIOD, GLFW.GLFW_KEY_SLASH, GLFW.GLFW_KEY_SEMICOLON, GLFW.GLFW_KEY_APOSTROPHE, GLFW.GLFW_KEY_MINUS,
			GLFW.GLFW_KEY_EQUAL, GLFW.GLFW_KEY_LEFT_BRACKET, GLFW.GLFW_KEY_RIGHT_BRACKET, GLFW.GLFW_KEY_BACKSLASH}) k.add(c);
		KEYS = k.stream().mapToInt(Integer::intValue).toArray();
	}

	/** Only while an ULTRAKILL menu is open; otherwise Esc stays Minecraft's (pause screen). */
	private static final int[] MENU_KEYS = {GLFW.GLFW_KEY_ESCAPE, GLFW.GLFW_KEY_ENTER, GLFW.GLFW_KEY_BACKSPACE};

	@Override
	public void onInitializeClient() {
		UltracraftConfig.load();
		UkLink.start();
		UcNet.registerClient();
		toggle = KeyBindingHelper.registerKeyBinding(new KeyMapping("key.ultracraft.toggle", InputConstants.Type.KEYSYM, GLFW.GLFW_KEY_F8, KeyMapping.Category.MISC));
		handsKey = KeyBindingHelper.registerKeyBinding(new KeyMapping("key.ultracraft.hands", InputConstants.Type.KEYSYM, GLFW.GLFW_KEY_V, KeyMapping.Category.MISC));
		ClientTickEvents.END_CLIENT_TICK.register(Ultracraft::clientTick);
		ClientChunkEvents.CHUNK_LOAD.register((level, chunk) -> WorldMesh.chunkLoaded(chunk.getPos().x, chunk.getPos().z));
		// ULTRAKILL's enemies are drawn by ULTRAKILL; their Minecraft stand-ins are only hitboxes
		EntityRendererRegistry.register(UltracraftCommon.UK_ENEMY, NoopRenderer::new);
		BlockEntityRenderers.register(UltracraftCommon.UK_SHOP_ENTITY, UkShopRenderer::new);
		BloodStains.register();
		UcSettingsScreen.register();
		UcTitleMenu.register();
		UkLauncher.register();
		// the V1 layer replaces Minecraft's own HUD while active; with Minecraft hands out, the hotbar and what goes
		// with items (hunger, armour, air, XP, the item's name, effects, the crosshair with its attack cooldown) come
		// back. Health stays ULTRAKILL's.
		Identifier[] vanilla = {
			VanillaHudElements.HOTBAR, VanillaHudElements.HEALTH_BAR, VanillaHudElements.FOOD_BAR, VanillaHudElements.ARMOR_BAR,
			VanillaHudElements.AIR_BAR, VanillaHudElements.EXPERIENCE_LEVEL, VanillaHudElements.INFO_BAR, VanillaHudElements.CROSSHAIR,
			VanillaHudElements.HELD_ITEM_TOOLTIP, VanillaHudElements.MOUNT_HEALTH, VanillaHudElements.STATUS_EFFECTS
		};
		List<Identifier> withHands = List.of(VanillaHudElements.HOTBAR, VanillaHudElements.FOOD_BAR, VanillaHudElements.ARMOR_BAR, VanillaHudElements.AIR_BAR,
			VanillaHudElements.EXPERIENCE_LEVEL, VanillaHudElements.INFO_BAR, VanillaHudElements.HELD_ITEM_TOOLTIP, VanillaHudElements.STATUS_EFFECTS,
			VanillaHudElements.CROSSHAIR);
		for (Identifier id : vanilla) {
			boolean handsShows = withHands.contains(id);
			// the air bubbles show whenever V1 runs out of breath, guns out or not (Minecraft drowns V1); so does hunger
			// once V1 is hungry (it stops Minecraft's healing, and starving hurts)
			boolean always = id.equals(VanillaHudElements.AIR_BAR);
			boolean whenHungry = id.equals(VanillaHudElements.FOOD_BAR);
			HudElementRegistry.replaceElement(id, old -> (ctx, t) -> {
				if (!active || (hands && handsShows)) {
					old.render(ctx, t);
				} else if (always || (whenHungry && hungry())) {
					// guns out, no hotbar under them: bubbles and hunger sit down at the bottom edge, where the hotbar
					// would be, instead of floating above nothing
					ctx.pose().pushMatrix();
					ctx.pose().translate(0f, 29f);
					old.render(ctx, t);
					ctx.pose().popMatrix();
				}
			});
		}
		HudElementRegistry.attachElementAfter(VanillaHudElements.MISC_OVERLAYS, Identifier.fromNamespaceAndPath("ultracraft", "v1"), (ctx, t) -> {
			renderV1(ctx);
			Teammates.render(Minecraft.getInstance(), ctx, t.getGameTimeDeltaPartialTick(false));
			Spectate.render(Minecraft.getInstance(), ctx);
		});
	}

	/** A weapon's own key: with Minecraft's items out, the guns come back for it. */
	static void gunKey(Minecraft mc) {
		if (hands) setHands(mc, false);
	}

	private static boolean hungry() {
		LocalPlayer p = Minecraft.getInstance().player;
		return p != null && p.getFoodData().getFoodLevel() < 18;
	}

	// ------------------------------------------------------------------ per frame

	private static int viewSent = -1;

	/** Minecraft's camera (F5): first person, behind V1, or in front of it; ULTRAKILL's camera does the same (VIEW). */
	private static void sendView(Minecraft mc) {
		int view = switch (mc.options.getCameraType()) {
			case FIRST_PERSON -> 0;
			case THIRD_PERSON_BACK -> 1;
			case THIRD_PERSON_FRONT -> 2;
		};
		if (view == viewSent || !UkLink.connected) return;
		viewSent = view;
		UkLink.send("VIEW " + view);
	}

	/** This frame's input already went to ULTRAKILL, the moment its last frame came in (UkFrame.waitForNext). */
	static boolean inputSentEarly;

	/**
	 * The input, as early as it can go: right when ULTRAKILL's frame arrives, ULTRAKILL is between frames, so input
	 * sent now is in its very next frame (sent later, while it's already drawing, it would wait a frame more).
	 */
	static void sendInputEarly() {
		Minecraft mc = Minecraft.getInstance();
		if (!UkLink.connected || mc.getWindow() == null) return;
		sendInput(mc);
		inputSentEarly = true;
	}

	private static void renderV1(GuiGraphics ctx) {
		Minecraft mc = Minecraft.getInstance();
		if (UkLink.connected && !inputSentEarly) sendInput(mc);
		inputSentEarly = false;
		if (!active && steveView) {
			// Steve with ULTRAKILL's enemies about: its layer, drawn from Steve's camera, over Minecraft's world
			sendMobsDrawn(mc);
			sendPuppets(mc);
			if (steveDrawn && UkFrame.updateForOverlay()) UkFrame.draw(ctx);
			return;
		}
		if (!active) {
			if (UkLink.connected && mc.player != null) {
				String where = UkLink.loadingScene.isEmpty() ? "" : " (" + UkLink.loadingScene + ")";
				ctx.drawString(mc.font, UkLink.ready ? "ULTRAKILL ready - press F8 to become V1" : "ULTRAKILL connected - loading V1..." + where, 4, 4, 0xFFFFFF00);
			}
			return;
		}
		sendMobsDrawn(mc);
		sendPuppets(mc);
		sendView(mc);
		UcKeybindsScreen.pollGunKeys(mc, mc.screen == null && !uiMode && mc.mouseHandler.isMouseGrabbed());
		Movement.frame(mc);
		// asleep: Minecraft's own view from the bed (its fade to morning), no V1 layer
		if (Movement.sleeping) return;
		if (UkFrame.updateForOverlay()) UkFrame.draw(ctx);
		renderP(mc, ctx);
	}

	private static final Map<Integer, Vec3> mobsDrawn = new HashMap<>();

	/**
	 * Where Minecraft draws each mob this frame (between ticks; ENTS only has tick positions): their stand-ins in
	 * ULTRAKILL follow, so a mob hides ULTRAKILL's enemies behind it exactly, even mid-knockback. Only mobs that moved.
	 */
	private static void sendMobsDrawn(Minecraft mc) {
		LocalPlayer self = mc.player;
		if (mc.level == null || self == null) return;
		var rates = mc.level.tickRateManager();
		var dt = mc.getDeltaTracker();
		StringBuilder sb = null;
		for (Entity e : mc.level.entitiesForRendering()) {
			if (e == self || !(e instanceof LivingEntity le) || !le.isAlive() || e instanceof UkEnemyEntity) continue;
			// a downed teammate watching the fight isn't there to be seen or hit
			if (e instanceof net.minecraft.world.entity.player.Player pl && pl.isSpectator()) continue;
			if (e.distanceToSqr(self) > 80 * 80) continue;
			Vec3 at = e.getPosition(dt.getGameTimeDeltaPartialTick(!rates.isEntityFrozen(e)));
			Vec3 was = mobsDrawn.put(e.getId(), at);
			if (was != null && was.distanceToSqr(at) < 1e-6) continue;
			if (sb == null) sb = new StringBuilder("EPOS ");
			sb.append(String.format(Locale.ROOT, "%d,%.3f,%.3f,%.3f;", e.getId(), at.x, at.y, at.z));
		}
		if (sb != null) UkLink.send(sb.toString());
		if (mobsDrawn.size() > 1024) mobsDrawn.clear();
	}

	private static String lastPuppets = "";

	/**
	 * The other players' ULTRAKILLs' enemies near us, where Minecraft draws their stand-ins this frame: our ULTRAKILL
	 * shows each as a puppet (the real enemy, its own brain off) that our shots hurt for real, in its owner's game.
	 * PUPS id,key,x,y,z,yaw,anim,dead,w,h;...
	 */
	private static void sendPuppets(Minecraft mc) {
		LocalPlayer self = mc.player;
		if (mc.level == null || self == null) return;
		String me = self.getUUID().toString();
		var dt = mc.getDeltaTracker();
		StringBuilder sb = new StringBuilder("PUPS ");
		for (Entity e : mc.level.entitiesForRendering()) {
			if (!(e instanceof UkEnemyEntity u) || u.ownerId().isEmpty() || u.ownerId().equals(me) || u.key().isEmpty()) continue;
			if (e.distanceToSqr(self) > 96 * 96) continue;
			Vec3 at = e.getPosition(dt.getGameTimeDeltaPartialTick(true));
			sb.append(String.format(Locale.ROOT, "%d,%s,%.3f,%.3f,%.3f,%.1f,%d,%d,%.2f,%.2f;", e.getId(), u.key(), at.x, at.y, at.z, u.ukYaw(), u.anim(), u.isDying() ? 1 : 0,
				e.getBbWidth(), e.getBbHeight()));
		}
		String msg = sb.toString();
		if (msg.equals(lastPuppets)) return;
		lastPuppets = msg;
		UkLink.send(msg);
	}

	private static void sendInput(Minecraft mc) {
		var win = mc.getWindow();
		// ULTRAKILL renders V1's layer at v1Height (same aspect) and we scale it up: each frame crosses between
		// the two games, so fewer pixels is directly more frames
		int fw = win.getWidth(), fh = win.getHeight();
		// at a shop's screen (using it, not just near it: full size costs frames) the small text is drawn sharp
		int cap = shopTouch && UltracraftConfig.sharpShop ? 0 : UltracraftConfig.v1Height;
		double scale = cap > 0 && fh > cap ? (double) cap / fh : 1.0;
		int w = Math.max(4, (int) Math.round(fw * scale) & ~3), h = Math.max(1, (int) Math.round(fh * scale));
		if (w != lastW || h != lastH) {
			lastW = w;
			lastH = h;
			UkLink.send("SIZE " + w + " " + h);
		}
		if (!active) return;
		long handle = win.handle();
		StringBuilder sb = new StringBuilder("IN ");
		// read the cursor straight from GLFW (with the cursor grabbed it is an unbounded virtual position)
		double[] cx = new double[1], cy = new double[1];
		GLFW.glfwGetCursorPos(handle, cx, cy);
		boolean ui = uiMode;
		boolean ingame = mc.screen == null && (mc.mouseHandler.isMouseGrabbed() || ui);
		boolean look = ingame && !ui;
		double dx = 0, dy = 0;
		if (look && haveCursor) {
			dx = cx[0] - lastCursorX;
			dy = cy[0] - lastCursorY;
		}
		lastCursorX = cx[0];
		lastCursorY = cy[0];
		haveCursor = look;
		mouseDx = 0;
		mouseDy = 0;
		if (ingame && ui) {
			// a menu is open: point at it (window coordinates scaled to the size ULTRAKILL renders at)
			double sx = win.getScreenWidth() > 0 ? (double) w / win.getScreenWidth() : 1.0;
			double sy = win.getScreenHeight() > 0 ? (double) h / win.getScreenHeight() : 1.0;
			UkLink.send(String.format(Locale.ROOT, "PTR %.1f %.1f", cx[0] * sx, cy[0] * sy));
		}
		// wheel notches as Windows reports them to ULTRAKILL (120 each)
		double notches = ingame ? wheel : 0;
		wheel = 0;
		sb.append(String.format(Locale.ROOT, "%.3f %.3f %.1f ", dx, dy, notches * 120.0));
		// Minecraft hands: clicks, number keys, E and Q are Minecraft's (use, mine, hotbar, inventory, drop); at a shop's
		// screen the clicks are the shop's
		boolean mcHands = hands && !ui;
		int buttons = 0;
		if (ingame) {
			for (int b = 0; b < 5; b++) if (GLFW.glfwGetMouseButton(handle, b) == GLFW.GLFW_PRESS) buttons |= 1 << b;
			if (mcHands && !shopTouch) buttons &= ~0b111;
		}
		sb.append(buttons);
		int bare = sb.length();
		// carried by Minecraft (gliding, riding, in bed): moving is Minecraft's (steering a horse or boat, jumping a
		// horse, Shift to get off), so ULTRAKILL's V1 doesn't also run, jump, dash or slide
		boolean carried = Movement.carried;
		if (ingame) {
			for (int k : KEYS) {
				if (mcHands && ((k >= GLFW.GLFW_KEY_0 && k <= GLFW.GLFW_KEY_9) || k == GLFW.GLFW_KEY_E || k == GLFW.GLFW_KEY_Q)) continue;
				if (carried && (k == GLFW.GLFW_KEY_W || k == GLFW.GLFW_KEY_A || k == GLFW.GLFW_KEY_S || k == GLFW.GLFW_KEY_D || k == GLFW.GLFW_KEY_SPACE
					|| k == GLFW.GLFW_KEY_LEFT_SHIFT || k == GLFW.GLFW_KEY_RIGHT_SHIFT || k == GLFW.GLFW_KEY_LEFT_CONTROL || k == GLFW.GLFW_KEY_RIGHT_CONTROL)) continue;
				if (GLFW.glfwGetKey(handle, k) == GLFW.GLFW_PRESS) sb.append(' ').append(k);
			}
			if (ui) {
				for (int k : MENU_KEYS) if (GLFW.glfwGetKey(handle, k) == GLFW.GLFW_PRESS) sb.append(' ').append(k);
			}
		}
		// someone is at the keyboard (bosses only come for a player who is)
		if (dx != 0 || dy != 0 || buttons != 0 || notches != 0 || sb.length() > bare) lastInputAt = System.currentTimeMillis();
		UkLink.send(sb.toString());
	}

	/** This world's P, ULTRAKILL-style ("48,242 P"). */
	public static String moneyText() {
		return String.format(Locale.ROOT, "%,d P", UkProgress.shownMoney);
	}

	/** When the player last moved, looked, clicked or pressed a key as V1. */
	static volatile long lastInputAt = System.currentTimeMillis();

	/** Big P gains come up at the side of the screen for a few seconds ("+15,000 P"), with the total below. */
	private static void renderP(Minecraft mc, GuiGraphics ctx) {
		long since = System.currentTimeMillis() - UkProgress.recentAt;
		if (UkProgress.recentGain <= 0 || since > 6000) return;
		int alpha = since < 5000 ? 255 : (int) (255 * (6000 - since) / 1000.0);
		if (alpha < 8) return;
		String gain = String.format(Locale.ROOT, "+%,d P", UkProgress.recentGain);
		String total = String.format(Locale.ROOT, "%,d P", UkProgress.shownMoney);
		int w = ctx.guiWidth(), y = ctx.guiHeight() / 2 + 24;
		ctx.pose().pushMatrix();
		ctx.pose().translate(w - 8, y);
		ctx.pose().scale(1.5f, 1.5f);
		ctx.drawString(mc.font, gain, -mc.font.width(gain), 0, (alpha << 24) | 0xFF4343);
		ctx.pose().popMatrix();
		ctx.drawString(mc.font, total, w - 8 - mc.font.width(total), y + 15, (alpha << 24) | 0xFFFFFF);
	}

	// ------------------------------------------------------------------ per tick

	private static boolean startedFullscreen;
	/** In a world (its progress asked for); leaving one resets ULTRAKILL's. */
	private static boolean inWorld;

	/** Ultracraft starts in windowed fullscreen (Minecraft's fullscreen is borderless, see WindowMixin); F11 toggles. */
	private static void startFullscreen(Minecraft mc) {
		if (startedFullscreen) return;
		startedFullscreen = true;
		if (!mc.options.fullscreen().get()) mc.options.fullscreen().set(true);
		mc.options.tutorialStep = net.minecraft.client.tutorial.TutorialSteps.NONE;
	}

	private static void clientTick(Minecraft mc) {
		startFullscreen(mc);
		DebugCommands.tick(mc);
		String msg;
		while ((msg = UkLink.INBOX.poll()) != null) handle(mc, msg);
		while (toggle.consumeClick()) {
			if (UkLink.ready && !downed) {
				active = !active;
				if (active) begin(mc);
				else end(mc);
			}
		}
		while (handsKey.consumeClick()) {
			if (active && !uiMode) setHands(mc, !hands);
		}
		// a hitstop never lasts this long: don't leave Minecraft frozen if ULTRAKILL never said it ended
		if (frozen && (System.currentTimeMillis() - frozenAt > 1500 || !active || !UkLink.connected)) setFrozen(mc, false);
		// Minecraft's pause menu (Esc, or the window losing focus) stops ULTRAKILL too: V1, enemies, projectiles, sound
		// as Steve without ULTRAKILL's enemies about, ULTRAKILL waits (frozen) until V1 is back
		boolean paused = (active || steveView) ? mc.isPaused() : everActive && mc.level != null;
		if (paused != pauseSent && UkLink.connected) {
			pauseSent = paused;
			UkLink.send("PAUSE " + (paused ? 1 : 0));
		}
		// Minecraft's frame limit (or VSync) changed: ULTRAKILL's cap follows it
		if (UkLink.connected && UltracraftConfig.ukFps == 0 && ++fpsCheck % 40 == 0) {
			int now = UltracraftConfig.ukFpsNow();
			if (now != fpsSent) {
				fpsSent = now;
				UltracraftConfig.sendOpts();
			}
		}
		LocalPlayer p = mc.player;
		if (mc.level == null) {
			// out of the world: Steve's view and V1's history start over
			steveView = false;
			everActive = false;
			// and nothing of its P, gear or upgrades goes on to the next world
			if (inWorld) {
				inWorld = false;
				UkProgress.shownMoney = 0;
				if (UkLink.connected) UkLink.send("WORLDRESET");
			}
		} else if (!inWorld && mc.player != null) {
			// into a world: its own P, gear and upgrades, straight away
			inWorld = true;
			// A new world gets one automatic V1 attempt. Do not reset this while staying in the same world:
			// pressing F8 to play as Steve remains the player's choice.
			if (!active) autoPending = true;
			if (UkLink.connected) UcNet.toServer("PROGRESS");
		}
		if (p == null || mc.level == null) return;
		safely("blood", () -> BloodStains.tick(mc.level));
		safely("oil", () -> OilDrips.tick(mc));
		if (autoPending && UltracraftConfig.autoV1 && UkLink.ready && !active && mc.screen == null) {
			// one-click play: become V1 as soon as both games are up
			autoPending = false;
			active = true;
			lastPlayer = p;
			begin(mc);
		}
		if (p != lastPlayer) {
			// respawned or changed dimension: put V1 where Minecraft put us
			lastPlayer = p;
			// out of the Cyber Grind's arenas (however: died, left), ULTRAKILL's sky over them goes
			if (p.level().dimension() != GrindArenas.DIMENSION && UkLink.connected) UkLink.send("SKY -");
			if (active) {
				UkLink.send("RESPAWN");
				teleportV1(p);
				lastExport = null;
			}
		}
		if (steveView && !active && UkLink.connected) {
			Spectate.tick(mc);
			steveTick(mc, p);
			return;
		}
		if (!active || !UkLink.connected) return;
		tick++;
		UkLink.Pose pose = UkLink.pose;
		boolean follow = Movement.tick(mc, p, pose);
		if (pose != null && follow) {
			// V1 is the player: Minecraft's player follows V1 (no Minecraft physics)
			p.getAbilities().flying = true;
			p.setDeltaMovement(Vec3.ZERO);
			p.setPos(pose.fx, pose.fy, pose.fz);
			p.setYRot(pose.yaw);
			p.setXRot(pose.pitch);
			p.setYHeadRot(pose.yaw);
			p.setOnGround(pose.onGround);
		} else if (Movement.carried) {
			// carried by Minecraft: V1 still looks where ULTRAKILL's camera looks (a glide and a mount steer by it)
			if (pose != null && !Movement.sleeping) {
				p.setYRot(pose.yaw);
				p.setXRot(pose.pitch);
				p.setYHeadRot(pose.yaw);
			}
		} else {
			// teleported, ULTRAKILL not there yet: hold still where Minecraft put us
			p.getAbilities().flying = true;
			p.setDeltaMovement(Vec3.ZERO);
		}
		BlockPos bp = p.blockPosition();
		if (lastExport == null || !lastExport.closerThan(bp, 2) || tick % 10 == 0) {
			exportBlocks(mc.level, bp);
			safely("fluids", () -> Fluids.export(mc.level, bp));
			lastExport = bp;
		}
		if (tick % 2 == 0) safely("light", () -> Lighting.export(mc, p));
		if (tick % 10 == 0) safely("weather", () -> Weather.export(mc));
		if (tick % 20 == 0) safely("shops", () -> ShopExport.export(mc.level, bp));
		if (tick % 10 == 0) {
			// at the keyboard in the last minute, in the game (not a menu or paused): bosses only come for a player who is
			boolean playing = System.currentTimeMillis() - lastInputAt < 60_000 && mc.screen == null && !mc.isPaused();
			if (playing != playingSent || tick % 200 == 0) {
				playingSent = playing;
				UcNet.toServer("PLAYING " + (playing ? 1 : 0));
			}
		}
		safely("steve", () -> SteveMode.tick(mc, p));
		exportEntities(mc, p);
		exportProjectiles(mc, p);
		WorldMesh.tick(mc.level, bp);
		if (UkLink.dead && !wasDead) {
			// V1 died in ULTRAKILL: die in Minecraft too (respawn goes through Minecraft); a Cyber Grind run ends there
			UcNet.toServer("DEAD");
		}
		wasDead = UkLink.dead;
	}

	/** As Steve with ULTRAKILL's enemies about: ULTRAKILL keeps getting the world, and V1's body follows Steve. */
	private static void steveTick(Minecraft mc, LocalPlayer p) {
		tick++;
		if (tick % 40 == 0) UkLink.send("STEVE 1");
		UkLink.send(String.format(java.util.Locale.ROOT, "STEVEPOS %.3f %.3f %.3f", p.getX(), p.getY(), p.getZ()));
		BlockPos bp = p.blockPosition();
		if (lastExport == null || !lastExport.closerThan(bp, 2) || tick % 10 == 0) {
			exportBlocks(mc.level, bp);
			safely("fluids", () -> Fluids.export(mc.level, bp));
			lastExport = bp;
		}
		if (tick % 2 == 0) safely("light", () -> Lighting.export(mc, p));
		if (tick % 10 == 0) safely("weather", () -> Weather.export(mc));
		if (tick % 20 == 0) safely("shops", () -> ShopExport.export(mc.level, bp));
		exportEntities(mc, p);
		exportProjectiles(mc, p);
		WorldMesh.tick(mc.level, bp);
	}

	/** Steve's view drawn this frame (the camera took a fresh frame from ULTRAKILL, not a third-person view). */
	public static boolean steveDrawn;

	private static void begin(Minecraft mc) {
		LocalPlayer p = mc.player;
		if (p == null) return;
		// the server knows us as V1 now: gamerules, flight, our P and gear (ServerOps "V1")
		UcNet.toServer("V1 1");
		everActive = true;
		if (steveView) {
			steveView = false;
			UkLink.send("STEVE 0");
		}
		lastInputAt = System.currentTimeMillis();
		originSent = false;
		lastBlocks = null;
		Movement.reset();
		teleportV1(p);
		lastExport = null;
		// ULTRAKILL may have restarted since, or the origin moved: send the far terrain, water and shops again
		WorldMesh.reset();
		Fluids.reset();
		ShopExport.reset();
		UkLink.send("HANDS " + (hands ? 1 : 0));
		viewSent = -1;
	}

	/** Back to Steve: nothing of V1's may linger (frozen ticks, ULTRAKILL's enemies' stand-ins). */
	private static void end(Minecraft mc) {
		setUiMode(mc, false);
		setFrozen(mc, false);
		SteveMode.reset();
		// let go of V1 if Minecraft was carrying it
		if (Movement.carried) UkLink.send("UNDRIVE 0 0 0");
		Movement.reset();
		shopTouch = false;
		shopNear = false;
		UkLink.send("ZOOM 1");
		UkLink.send("VIEW 0");
		viewSent = 0;
		if (UltracraftConfig.steveEnemies && UkLink.ready) {
			// ULTRAKILL's enemies stay: drawn from Steve's camera, still fighting (their stand-ins stay in the world)
			steveView = true;
			UkLink.send("STEVE 1");
			UcNet.toServer("V1 0 keep");
		} else {
			UcNet.toServer("V1 0");
		}
	}

	/** In a duel (Duels): whose V1 our shots hurt, or -1. */
	static int duelWith = -1;

	/** C:DUEL id | C:DUEL -: a duel began against that player, or it's over (a V1 that died in it gets up again). */
	private static void duel(Minecraft mc, String rest) {
		boolean on = !rest.equals("-");
		duelWith = on ? Integer.parseInt(rest) : -1;
		UkLink.send("DUEL " + rest);
		if (!on && mc.player != null) {
			if (UkLink.dead) UkLink.send("RESPAWN");
			UkLink.send("FULLHEAL");
			if (active) teleportV1(mc.player);
		}
	}

	/** Down in a boss fight (BossParty): watching a teammate, with ULTRAKILL drawing the fight from that view. */
	public static boolean downed;

	static void setDowned(Minecraft mc, boolean on, boolean dead) {
		if (on == downed) return;
		downed = on;
		if (!on) Spectate.reset();
		if (on) {
			setUiMode(mc, false);
			setFrozen(mc, false);
			Movement.reset();
			active = false;
			steveView = true;
			UkLink.send("STEVE 1");
			UkLink.send("DOWNED 1");
		} else {
			UkLink.send("DOWNED 0");
			if (dead) {
				// the fight was lost: dead like everyone; V1 comes back with Minecraft's respawn, as after any death
				steveView = false;
				UkLink.send("STEVE 0");
				active = true;
				return;
			}
			// back up: V1 again, right where Minecraft put us
			UkLink.send("RESPAWN");
			active = true;
			begin(mc);
			UkLink.send("HUD <color=#FFD040>BACK ON YOUR FEET</color>");
		}
	}

	/** Become V1 or Steve (F8). */
	static void setActive(Minecraft mc, boolean on) {
		if (on == active || (on && !UkLink.ready)) return;
		active = on;
		if (on) begin(mc);
		else end(mc);
	}

	static void setHands(Minecraft mc, boolean on) {
		hands = on;
		UkLink.send("HANDS " + (on ? 1 : 0));
		if (mc.player != null) {
			mc.player.displayClientMessage(net.minecraft.network.chat.Component.literal(on ? "Minecraft hands (V for guns)" : "V1's guns (V for Minecraft hands)"), true);
		}
	}

	/** ULTRAKILL's hitstop: Minecraft's world stops for as long (mobs, projectiles, particles; like /tick freeze). */
	private static void setFrozen(Minecraft mc, boolean on) {
		if (on == frozen) return;
		frozen = on;
		frozenAt = System.currentTimeMillis();
		MinecraftServer server = mc.getSingleplayerServer();
		if (server != null) server.execute(() -> server.tickRateManager().setFrozen(on));
	}

	private static void setUiMode(Minecraft mc, boolean on) {
		uiMode = on;
		if (on && active) {
			mc.mouseHandler.releaseMouse();
		} else if (mc.screen == null && mc.player != null) {
			mc.mouseHandler.grabMouse();
		}
	}

	private static final java.util.Set<String> failed = new java.util.HashSet<>();

	/** Extras that must never take the game down with them: a failure is logged once and the tick goes on. */
	private static void safely(String what, Runnable r) {
		try {
			r.run();
		} catch (RuntimeException e) {
			if (failed.add(what)) org.slf4j.LoggerFactory.getLogger("ultracraft").error("{} export failed", what, e);
		}
	}

	static void teleportV1(LocalPlayer p) {
		boolean rebase = false;
		if (originSent && originAt != null && (originDim != p.level().dimension()
			|| Math.max(Math.abs(p.getX() - originAt.x), Math.abs(p.getZ() - originAt.z)) > REBASE || Math.abs(p.getY() - originAt.y) > REBASE)) {
			// far from the origin (the Cyber Grind's arenas, another dimension, a long /tp): it moves here, and
			// ULTRAKILL gets the terrain, water and shops around it afresh (its enemies and bosses stay put)
			originSent = false;
			rebase = true;
			lastBlocks = null;
			lastExport = null;
			WorldMesh.reset();
			Fluids.reset();
			ShopExport.reset();
		}
		if (!originSent) {
			UkLink.send(String.format(Locale.ROOT, "ORIGIN %.3f %.3f %.3f", p.getX(), p.getY(), p.getZ()) + (rebase ? " keep" : ""));
			originSent = true;
			originAt = p.position();
			originDim = p.level().dimension();
		}
		exportBlocks(p.level() instanceof ClientLevel cl ? cl : null, p.blockPosition());
		UkLink.send(String.format(Locale.ROOT, "TP %.3f %.3f %.3f %.2f %.2f", p.getX(), p.getY() + 0.05, p.getZ(), p.getYRot(), p.getXRot()));
	}

	private static void handle(Minecraft mc, String msg) {
		if (msg.equals("READY") && mc.player != null && mc.player.connection != null) {
			mc.gui.getChat().addMessage(net.minecraft.network.chat.Component.literal("[Ultracraft] ULTRAKILL is ready. "
				+ (UltracraftConfig.autoV1 ? "V1 will activate automatically." : "Press F8 to become V1.")));
			// READY may arrive after the player has already joined; the normal tick performs the transition when the
			// loading screen is gone. This also covers a bridge restart without requiring another world join.
			if (UltracraftConfig.autoV1 && mc.level != null && !active) autoPending = true;
			UcCheats.sendServer();
		} else if (msg.equals("CONNECTED")) {
			// a (re)started ULTRAKILL needs our window size again, and whether we're paused
			lastW = -1;
			pauseSent = false;
			Fluids.reset();
			// a restarted ULTRAKILL knows none of our textures, shops or sky yet, nor the settings
			UltracraftConfig.sendOpts();
			CutoutTextures.reset();
			ShopExport.reset();
			Weather.reset();
			UcNet.toServer("PROGRESS");
			UcCheats.sendServer();
		} else if (msg.equals("DISCONNECTED")) {
			active = false;
			autoPending = true;
			end(mc);
		} else if (msg.startsWith("FREEZE ")) {
			setFrozen(mc, msg.endsWith("1"));
		} else if (msg.startsWith("UKE ")) {
			Lighting.ukEnemies(msg.substring(4));
			UcNet.toServer(msg);
		} else if (msg.startsWith("UI ")) {
			setUiMode(mc, msg.endsWith("1"));
		} else if (msg.matches("^(SHOPINFO|SUNINFO|PUSHINFO|NUKETEST|SPAWNED|MOVEINFO|WATERINFO|GROUND|LIGHTINFO|STAININFO|BOSSINFO|GEARINFO|BOSSSPAWNED|SHOPPRESSED|CUTINFO|FXINFO|PUPINFO|BINDINFO|SHOPRQ|FRAMESNAPPED|SPAWNABLES|MUSICINFO|ARMINFO|COMBATTEST|GUNINFO|MPINFO) .*")) {
			// debug answers (DebugCommands "uk ...")
			org.slf4j.LoggerFactory.getLogger("ultracraft").info("[uk] {}", msg);
		} else if (msg.startsWith("SHOPZONE ")) {
			// SHOPZONE touch near
			String[] a = msg.split(" ");
			boolean touch = a[1].equals("1");
			if (touch != shopTouch) UcNet.toServer("SHOP " + (touch ? 1 : 0));
			shopTouch = touch;
			shopNear = a.length > 2 && a[2].equals("1");
		} else if (msg.startsWith("SONGS ")) {
			UcMusicScreen.songs(msg.substring(6));
		} else if (msg.startsWith("FIGHT ")) {
			UcThemes.fight(mc, msg.endsWith("1"));
		} else if (msg.startsWith("STAINS ")) {
			BloodStains.add(mc.level, msg.substring(7));
		} else if (msg.startsWith("OILS ")) {
			BloodStains.addOil(mc.level, msg.substring(5));
		} else if (msg.startsWith("OILBURN ")) {
			String[] a = msg.split(" ");
			BloodStains.burnOil(Float.parseFloat(a[1]), Float.parseFloat(a[2]), Float.parseFloat(a[3]));
		} else if (msg.startsWith("OILED ")) {
			String[] a = msg.split(" ");
			OilDrips.set(Integer.parseInt(a[1]), Integer.parseInt(a[2]));
		} else if (msg.equals("STAINCLEAR")) {
			BloodStains.clear();
		} else if (msg.startsWith("STAINSIZE ")) {
			BloodStains.setSize(Float.parseFloat(msg.substring(10).trim()));
		} else if (msg.startsWith("UKPREFS ")) {
			// UKPREFS key=type:value;...: ULTRAKILL's own settings, for the settings screen
			UcSettingsScreen.reported(msg.substring(8));
		} else if (SERVER_OPS.matcher(msg).lookingAt()) {
			// what our ULTRAKILL did to the shared world (blocks, blasts, mobs, P, purchases, parries, bosses, its
			// enemies): the server (this process in singleplayer, the host's in multiplayer) applies it as our player
			UcNet.toServer(msg);
		}
	}

	/** ULTRAKILL's messages that act on the world, handled by the server (ServerOps). */
	private static final java.util.regex.Pattern SERVER_OPS = java.util.regex.Pattern.compile(
		"^(V1STATE|FX|SLAM|RAIL|BOOM|HIT|FIRE|DMG|WHIP|MKNOCK|PIMPACT|PHOLD|PRELEASE|PARRY|PEARN|PADD|GEARADD|UPBUY|BOSSPOS|BOSSDEAD|BOSSGONE|GRIND|SPAWNS|UKDEAD|GRINDGONE|UKDIE|PHIT|SHURT|EQUIP|NOSPAWN|STYLE) ");

	/** A line from the server for our Minecraft side ("C:..." in UcNet), on the client thread. */
	static void fromServer(String msg) {
		if (msg.startsWith("DOWNED ")) {
			setDowned(Minecraft.getInstance(), msg.startsWith("DOWNED 1"), msg.endsWith("dead"));
		} else if (msg.startsWith("WATCH ")) {
			Spectate.watch(msg.substring(6));
		} else if (msg.equals("SETTINGS")) {
			Minecraft.getInstance().setScreen(new UcSettingsScreen(null));
		} else if (msg.startsWith("DOWNS")) {
			Teammates.downs(msg.length() > 6 ? msg.substring(6) : "");
		} else if (msg.startsWith("DUEL ")) {
			duel(Minecraft.getInstance(), msg.substring(5).trim());
		} else if (msg.startsWith("V1S ")) {
			UcNet.clientV1s(msg.substring(4));
		} else if (msg.startsWith("PGAIN ")) {
			UkProgress.gained(Integer.parseInt(msg.substring(6).trim()));
		} else if (msg.startsWith("THEME ")) {
			UcThemes.theme(msg.substring(6).trim());
		}
	}

	interface ServerTask {
		void run(MinecraftServer server, ServerPlayer player);
	}

	static void runOnServer(Minecraft mc, ServerTask task) {
		MinecraftServer server = mc.getSingleplayerServer();
		if (server == null || mc.player == null) return;
		var uuid = mc.player.getUUID();
		server.execute(() -> {
			ServerPlayer sp = server.getPlayerList().getPlayer(uuid);
			if (sp != null) task.run(server, sp);
		});
	}

	// ------------------------------------------------------------------ world export

	private static final int RH = 12, RDOWN = 8, RUP = 14;

	/** Solid blocks around the player as boxes: exposed full cubes merged into vertical runs, partial shapes as-is. */
	private static void exportBlocks(ClientLevel level, BlockPos c) {
		if (level == null) return;
		StringBuilder sb = new StringBuilder(64 * 1024);
		sb.append("BLOCKS ");
		BlockPos.MutableBlockPos m = new BlockPos.MutableBlockPos();
		for (int x = c.getX() - RH; x <= c.getX() + RH; x++) {
			for (int z = c.getZ() - RH; z <= c.getZ() + RH; z++) {
				int runStart = Integer.MIN_VALUE;
				for (int y = c.getY() - RDOWN; y <= c.getY() + RUP + 1; y++) {
					boolean fullExposed = false;
					if (y <= c.getY() + RUP) {
						m.set(x, y, z);
						BlockState s = level.getBlockState(m);
						if (!s.isAir()) {
							VoxelShape shape = s.getCollisionShape(level, m);
							if (!shape.isEmpty()) {
								if (s.isCollisionShapeFullBlock(level, m)) {
									fullExposed = exposed(level, x, y, z, m);
								} else {
									for (AABB b : shape.toAabbs()) {
										sb.append(String.format(Locale.ROOT, "%.4f,%.4f,%.4f,%.4f,%.4f,%.4f;", x + b.minX, y + b.minY, z + b.minZ, x + b.maxX, y + b.maxY, z + b.maxZ));
									}
								}
							}
						}
					}
					if (fullExposed && runStart == Integer.MIN_VALUE) runStart = y;
					if (!fullExposed && runStart != Integer.MIN_VALUE) {
						sb.append(x).append(',').append(runStart).append(',').append(z).append(',')
							.append(x + 1).append(',').append(y).append(',').append(z + 1).append(';');
						runStart = Integer.MIN_VALUE;
					}
				}
			}
		}
		// unchanged surroundings: don't make ULTRAKILL parse (and garbage-collect) the same list again
		String s = sb.toString();
		long now = System.currentTimeMillis();
		if (s.equals(lastBlocks) && now - lastBlocksAt < 5000) return;
		lastBlocks = s;
		lastBlocksAt = now;
		UkLink.send(s);
	}

	private static boolean exposed(ClientLevel level, int x, int y, int z, BlockPos.MutableBlockPos m) {
		int[][] d = {{1, 0, 0}, {-1, 0, 0}, {0, 1, 0}, {0, -1, 0}, {0, 0, 1}, {0, 0, -1}};
		for (int[] o : d) {
			m.set(x + o[0], y + o[1], z + o[2]);
			if (!level.getBlockState(m).isCollisionShapeFullBlock(level, m)) {
				m.set(x, y, z);
				return true;
			}
		}
		m.set(x, y, z);
		return false;
	}

	/** Projectiles flying around V1 (not V1's own): ULTRAKILL tracks them for parries and decides hits on V1. */
	private static void exportProjectiles(Minecraft mc, LocalPlayer self) {
		StringBuilder sb = new StringBuilder("PROJS ");
		for (Entity e : mc.level.entitiesForRendering()) {
			if (!(e instanceof Projectile pr) || e instanceof FishingHook || pr.getOwner() == self) continue;
			if (e.distanceToSqr(self) > 48 * 48) continue;
			Vec3 v = e.getDeltaMovement();
			if (v.lengthSqr() < 1e-4) continue; // stuck in a block
			String type = BuiltInRegistries.ENTITY_TYPE.getKey(e.getType()).getPath();
			sb.append(String.format(Locale.ROOT, "%d,%s,%.3f,%.3f,%.3f,%.4f,%.4f,%.4f,%.3f;",
				e.getId(), type, e.getX(), e.getY() + e.getBbHeight() * 0.5, e.getZ(), v.x, v.y, v.z, Math.max(e.getBbWidth(), e.getBbHeight())));
		}
		UkLink.send(sb.toString());
	}

	private static void exportEntities(Minecraft mc, LocalPlayer self) {
		StringBuilder sb = new StringBuilder("ENTS ");
		for (Entity e : mc.level.entitiesForRendering()) {
			if (e instanceof net.minecraft.world.entity.boss.enderdragon.EndCrystal crystal) {
				if (!crystal.isAlive() || e.distanceToSqr(self) > 80 * 80) continue;
				sb.append(String.format(Locale.ROOT, "%d,end_crystal,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,1.0,1.0,0,0.0;", e.getId(), e.getX(), e.getY(), e.getZ(),
					e.getBbWidth(), e.getBbHeight(), e.getBbHeight() * 0.5));
				continue;
			}
			// ULTRAKILL's own enemies' stand-ins are ULTRAKILL's already
			if (e == self || !(e instanceof LivingEntity le) || !le.isAlive() || e instanceof UkEnemyEntity || e.isSpectator()) continue;
			// another player who is V1: ULTRAKILL draws V1's body there, and its enemies go for them as for us (a
			// teammate who is down in a boss fight watches as a spectator: not there, and nobody's target)
			boolean v1 = UcNet.isV1(e);
			if (e.distanceToSqr(self) > (v1 ? 160 * 160 : 80 * 80)) continue;
			String type = v1 ? "v1" : BuiltInRegistries.ENTITY_TYPE.getKey(e.getType()).getPath();
			// hostile mobs (and other V1s) are what ULTRAKILL's enemies go for
			int hostile = e instanceof Enemy || v1 ? 1 : 0;
			// on fire: ULTRAKILL's own fire burns on it (EntityMixin keeps Minecraft's flames off it meanwhile)
			sb.append(String.format(Locale.ROOT, "%d,%s,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.1f,%.1f,%d,%.1f,%d;",
				e.getId(), type, e.getX(), e.getY(), e.getZ(), e.getBbWidth(), e.getBbHeight(), e.getEyeHeight(), le.getHealth(), le.getMaxHealth(), hostile,
				v1 ? e.getYHeadRot() : e.getYRot(), e.isOnFire() ? 1 : 0));
		}
		UkLink.send(sb.toString());
	}
}
