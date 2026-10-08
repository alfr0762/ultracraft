package dev.ultracraft;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.function.Consumer;
import net.fabricmc.fabric.api.client.screen.v1.ScreenEvents;
import net.fabricmc.fabric.api.client.screen.v1.Screens;
import net.minecraft.client.Minecraft;
import net.minecraft.client.OptionInstance;
import net.minecraft.client.Options;
import net.minecraft.client.gui.components.AbstractWidget;
import net.minecraft.client.gui.components.Button;
import net.minecraft.client.gui.components.Tooltip;
import net.minecraft.client.gui.screens.PauseScreen;
import net.minecraft.client.gui.screens.Screen;
import net.minecraft.client.gui.screens.TitleScreen;
import net.minecraft.client.gui.screens.options.OptionsScreen;
import net.minecraft.client.gui.screens.options.OptionsSubScreen;
import net.minecraft.network.chat.Component;

/**
 * Ultracraft's settings, by category: enemies and bosses, gameplay, the shop and rewards, performance, music, cheats,
 * ULTRAKILL's own settings (sensitivity, field of view, screen shake...) as Ultracraft plays it, and its controls.
 * Opened with the "Ultracraft" button on the pause menu, the title screen and Minecraft's Options.
 */
public final class UcSettingsScreen extends OptionsSubScreen {
	/** One of ULTRAKILL's settings: its pref name, its type (f float, i int, b bool) and its default. */
	record UkPref(String key, char type, String caption, String def, int min, int max, String tooltip) {}

	static final List<UkPref> UK_PREFS = List.of(
		new UkPref("difficulty", 'i', "ULTRAKILL Difficulty", "2", 0, 4,
			"ULTRAKILL's difficulty for V1 and its enemies while playing Ultracraft: Harmless gives V1 200 health; harder ones make enemies faster and deadlier. Enemies already out keep theirs."),
		new UkPref("mouseSensitivity", 'f', "Mouse Sensitivity", "50", 1, 100, "ULTRAKILL's mouse sensitivity (its default is 50)."),
		new UkPref("fieldOfView", 'f', "Field of View", "105", 60, 160, "ULTRAKILL's field of view; Minecraft's world is drawn to match."),
		new UkPref("screenShake", 'f', "Screen Shake", "1", 0, 100, "How hard explosions and hits shake the camera."),
		new UkPref("cameraTilt", 'b', "Camera Tilt", "true", 0, 1, "The camera leans into strafes and slides."),
		new UkPref("parryFlash", 'b', "Parry Flash", "true", 0, 1, "The white flash on a parry."),
		new UkPref("weaponHoldPosition", 'i', "Weapon Position", "0", 0, 2, "Which side V1 holds the guns on."),
		new UkPref("mouseReverseY", 'b', "Invert Mouse Y", "false", 0, 1, "Mouse up looks down."),
		new UkPref("bloodEnabled", 'b', "Blood", "true", 0, 1, "ULTRAKILL's blood (it still heals V1 with this off)."),
		new UkPref("allVolume", 'f', "ULTRAKILL Volume", "1", 0, 100, "The volume of everything ULTRAKILL plays."),
		new UkPref("musicVolume", 'f', "ULTRAKILL Music", "0.6", 0, 100, "ULTRAKILL's music (fights, bosses, the Cyber Grind)."));

	private static final String[] UK_DIFFICULTY = {"Harmless", "Lenient", "Standard", "Violent", "Brutal"};

	/** The ones on the Music page rather than ULTRAKILL Settings. */
	private static final List<String> VOLUMES = List.of("allVolume", "musicVolume");

	/** What ULTRAKILL said its settings are (UKPREFS), for the ones Ultracraft hasn't set itself. */
	static final Map<String, String> reported = new HashMap<>();

	public UcSettingsScreen(Screen last) {
		super(last, Minecraft.getInstance().options, Component.literal("Ultracraft Settings"));
	}

	/** The "Ultracraft" button on the pause menu, the title screen and Minecraft's Options. */
	static void register() {
		ScreenEvents.AFTER_INIT.register((mc, screen, w, h) -> {
			// why a connection ended goes in the log too (the screen alone is lost once closed)
			if (screen instanceof net.minecraft.client.gui.screens.DisconnectedScreen) {
				org.slf4j.LoggerFactory.getLogger("ultracraft").info("[disconnected] {}", screen.getNarrationMessage().getString());
			}
			if (screen instanceof PauseScreen || screen instanceof OptionsScreen || screen instanceof TitleScreen) {
				Screens.getButtons(screen).add(Button.builder(Component.literal("Ultracraft..."), b -> mc.setScreen(new UcSettingsScreen(screen)))
					.bounds(4, 4, 90, 20).tooltip(Tooltip.create(Component.literal("Ultracraft and ULTRAKILL settings"))).build());
			}
		});
	}

	/** UKPREFS key=type:value;...: ULTRAKILL's own settings as it has them. */
	static void reported(String data) {
		for (String rec : data.split(";")) {
			int eq = rec.indexOf('=');
			if (eq > 0) reported.put(rec.substring(0, eq), rec.substring(eq + 1));
		}
	}

	@Override
	protected void addOptions() {
		list.addSmall(open("Enemies & Bosses", "Which enemies come, ULTRAKILL's bosses and arenas.", UcSettingsScreen::enemies),
			open("Gameplay", "Becoming V1, what breaks blocks, impact frames.", UcSettingsScreen::gameplay));
		list.addSmall(open("Shop & Rewards", "The OP Shop, the shop's screen, style rewards.", UcSettingsScreen::shop),
			open("Performance", "ULTRAKILL's resolution and frame rate, effects, blood stains, terrain range, and a Low-End PC preset.", UcSettingsScreen::performance));
		list.addSmall(open("Music", "ULTRAKILL's music in fights: which song, boss themes, volumes.", UcSettingsScreen::music),
			open("Cheats", "ULTRAKILL's Sandbox cheats and a few of Ultracraft's.", UcCheats::fill));
		list.addSmall(open("ULTRAKILL Settings", "Sensitivity, field of view, screen shake... as Ultracraft plays it (ULTRAKILL's own settings stay as they are).",
			UcSettingsScreen::ukSettings), Button.builder(Component.literal("ULTRAKILL Controls..."), b -> minecraft.setScreen(new UcKeybindsScreen(this)))
			.tooltip(Tooltip.create(Component.literal("Rebind ULTRAKILL's keys."))).build());
	}

	/** Debug: a category's page by its short name. */
	static Screen page(String name) {
		UcSettingsScreen root = new UcSettingsScreen(null);
		return switch (name) {
			case "enemies" -> new Category(root, "Enemies & Bosses", UcSettingsScreen::enemies);
			case "gameplay" -> new Category(root, "Gameplay", UcSettingsScreen::gameplay);
			case "shop" -> new Category(root, "Shop & Rewards", UcSettingsScreen::shop);
			case "performance" -> new Category(root, "Performance", UcSettingsScreen::performance);
			case "music" -> new Category(root, "Music", UcSettingsScreen::music);
			case "cheats" -> new Category(root, "Cheats", UcCheats::fill);
			case "uk" -> new Category(root, "ULTRAKILL Settings", UcSettingsScreen::ukSettings);
			default -> root;
		};
	}

	private Button open(String name, String tooltip, Consumer<Category> fill) {
		return Button.builder(Component.literal(name + "..."), b -> minecraft.setScreen(new Category(this, name, fill)))
			.tooltip(Tooltip.create(Component.literal(tooltip))).build();
	}

	@Override
	public void removed() {
		super.removed();
		UltracraftConfig.save();
	}

	/** One category's page. */
	static final class Category extends OptionsSubScreen {
		private final Consumer<Category> fill;

		Category(Screen last, String name, Consumer<Category> fill) {
			super(last, Minecraft.getInstance().options, Component.literal(name));
			this.fill = fill;
		}

		@Override
		protected void addOptions() {
			fill.accept(this);
		}

		void add(OptionInstance<?>... options) {
			list.addSmall(options);
		}

		void add(AbstractWidget a, AbstractWidget b) {
			list.addSmall(a, b);
		}

		void header(String text) {
			list.addHeader(Component.literal(text));
		}

		Screen self() {
			return this;
		}

		/** The page again, showing settings changed behind its back (a preset). */
		void reopen() {
			Minecraft.getInstance().setScreen(new Category(lastScreen, getTitle().getString(), fill));
		}

		@Override
		public void removed() {
			super.removed();
			UltracraftConfig.save();
		}
	}

	// ------------------------------------------------------------------ the categories

	private static void enemies(Category c) {
		c.add(bool("Minecraft Mobs", "Minecraft's monsters spawn. Bosses (the Ender Dragon, the Wither, Elder Guardians, the Warden) always do.",
				UltracraftConfig.mcMobs, v -> UltracraftConfig.mcMobs = v),
			bool("ULTRAKILL Enemies", "ULTRAKILL's enemies spawn in the dark, like Minecraft's monsters: which ones depends on the biome and the dimension. Its bosses and the Cyber Grind still come.",
				UltracraftConfig.ukSpawns, v -> {
					UltracraftConfig.ukSpawns = v;
					CyberGrind.sendState();
				}),
			bool("ULTRAKILL Bosses", "ULTRAKILL's bosses come for V1 now and then, with a warning first.", UltracraftConfig.bosses, v -> UltracraftConfig.bosses = v),
			slider("Time Between Bosses", "About how many minutes of play pass between bosses.", 5, 60, UltracraftConfig.bossMinutes, v -> v + " min",
				v -> UltracraftConfig.bossMinutes = v),
			slider("Boss Difficulty", "How hard bosses are. Auto: they get harder the more of them V1 has beaten.", 0, 5, UltracraftConfig.bossDifficulty,
				v -> v == 0 ? "Auto" : UkBosses.DIFFICULTY[v - 1], v -> UltracraftConfig.bossDifficulty = v),
			slider("Trait Chance", "How often a boss comes with traits (modifiers such as Radiant or Volatile); about a quarter of those bring two.", 0, 100,
				UltracraftConfig.traitChance, v -> v + "%", v -> UltracraftConfig.traitChance = v),
			bool("Cyber Grind Arenas", "Starting the Cyber Grind at a shop takes you into its own arenas: 50 of them, each after one of ULTRAKILL's levels, the next one once you've cleared its waves. Each has a temporary shop somewhere: its screen takes you back. Off: the waves come round the shop you started it from.",
				UltracraftConfig.grindArenas, v -> UltracraftConfig.grindArenas = v),
			bool("ULTRAKILL Arenas", "Arenas themed after ULTRAKILL's layers of Hell generate in new parts of the world, each with a boss waiting inside.",
				UltracraftConfig.arenas, v -> UltracraftConfig.arenas = v),
			bool("ULTRAKILL While Steve", "Back as Steve (F8), ULTRAKILL's enemies stay and keep fighting: you see them, and they come for Steve. Off: they wait, frozen, until you're V1 again.",
				UltracraftConfig.steveEnemies, v -> UltracraftConfig.steveEnemies = v));
	}

	private static void gameplay(Category c) {
		c.add(bool(Component.translatable("ultracraft.menu.setting").getString(), Component.translatable("ultracraft.menu.setting.tooltip").getString(),
				UltracraftConfig.terminalMenu, v -> UltracraftConfig.terminalMenu = v),
			bool("Become V1 Automatically", "Become V1 as soon as ULTRAKILL is ready (otherwise F8).", UltracraftConfig.autoV1, v -> UltracraftConfig.autoV1 = v),
			bool("V1 Breaks Blocks", "V1's guns, punches, slams and blasts break blocks.", UltracraftConfig.playerBlockDamage, v -> UltracraftConfig.playerBlockDamage = v),
			bool("Enemies Break Blocks", "ULTRAKILL's enemies' shots, beams, blasts and fire break blocks.", UltracraftConfig.enemyBlockDamage,
				v -> UltracraftConfig.enemyBlockDamage = v),
			slider("Impact Frames", "How long ULTRAKILL's impact frames (the freeze on big hits) last: 0.1x to 3x.", 1, 30, Math.round(UltracraftConfig.impactFrames * 10f),
				v -> String.format(Locale.ROOT, "%.1fx", v / 10f), v -> UltracraftConfig.impactFrames = v / 10f),
			bool("Start ULTRAKILL", "Starting Minecraft starts ULTRAKILL too (through Steam), and closing Minecraft closes it.", UltracraftConfig.launchUltrakill,
				v -> UltracraftConfig.launchUltrakill = v),
			bool("Teammate Markers", "In multiplayer: the other players' names, health and distance over their heads, seen through walls, and at the screen's edge when they're off it.",
				UltracraftConfig.teammateMarkers, v -> UltracraftConfig.teammateMarkers = v));
	}

	private static void shop(Category c) {
		c.add(bool("Style Rewards", "Kills at ULTRAKILL's top style ranks (S and up) pay extra experience and loot, and a streak of them pays more: diamonds and golden apples at SSS, netherite at ULTRAKILL.",
				UltracraftConfig.styleRewards, v -> UltracraftConfig.styleRewards = v),
			bool("OP Shop", "The shop's Upgrades page goes much further: every weapon's and arm's Power up to 1500%, and blast sizes (Payload, Shockwave, the mini nuke) up to 1500%.",
				UltracraftConfig.opShop, v -> {
					UltracraftConfig.opShop = v;
					resendUpgrades();
				}),
			bool("Sharp Shop Screen", "Using a shop, ULTRAKILL draws at full resolution so the text is sharp (costs frames while you use it).",
				UltracraftConfig.sharpShop, v -> UltracraftConfig.sharpShop = v));
	}

	private static void performance(Category c) {
		c.add(Button.builder(Component.literal("Low-End PC Preset"), b -> preset(c, UltracraftConfig::lowEndPreset))
				.tooltip(Tooltip.create(Component.literal("Everything on this page at its cheapest that still plays well: 480p, Low effects, 1,500 blood stains, no extra gore, 64 blocks of terrain, Low-Latency Frames off. Each can still be changed below."))).build(),
			Button.builder(Component.literal("Reset to Defaults"), b -> preset(c, UltracraftConfig::performanceDefaults))
				.tooltip(Tooltip.create(Component.literal("Everything on this page back as it came: 720p, frame rate matching Minecraft, High effects, all blood stains, extra gore, 128 blocks of terrain."))).build());
		c.add(slider("ULTRAKILL Resolution", "How tall ULTRAKILL draws its picture (scaled up to fill the window). Lower = much faster; this is the biggest frame rate setting.",
				0, 6, resIndex(), v -> RES_NAMES[v], v -> UltracraftConfig.v1Height = RES[v]),
			slider("ULTRAKILL FPS Cap", "How many frames a second ULTRAKILL draws. Match Minecraft (the far left) keeps it in step with Minecraft's own frame limit, the smoothest. It shares the graphics card with Minecraft: a lower cap leaves Minecraft more.",
				2, 24, UltracraftConfig.ukFps == 0 ? 2 : UltracraftConfig.ukFps / 10, v -> v == 2 ? "Match Minecraft" : v * 10 + " FPS",
				v -> UltracraftConfig.ukFps = v == 2 ? 0 : v * 10),
			bool("Frame Lock Step", "Minecraft waits for each of ULTRAKILL's frames, so the two always match: the smoothest when ULTRAKILL keeps up. When it can't (it stops waiting by itself for a few seconds then), or if the game stutters, turn this off.",
				UltracraftConfig.lockStep, v -> UltracraftConfig.lockStep = v),
			bool("Low-Latency Frames", "ULTRAKILL hands each frame to Minecraft as soon as it's drawn, rather than a frame or two later: less input lag. On a slow graphics card, off may give a few more frames a second.",
				UltracraftConfig.lowLatency, v -> UltracraftConfig.lowLatency = v));
		c.add(slider("Effects Quality", "How much ULTRAKILL draws for its effects. High: as ULTRAKILL has them. Medium: simpler explosions and fire, no environment particles, less gore at once. Low: also simpler spawn effects, no hit sparks, far less gore at once and no shadows.",
				0, 2, UltracraftConfig.effects, v -> FX_NAMES[v], v -> UltracraftConfig.effects = v),
			slider("Blood Stains", "How many of ULTRAKILL's blood stains stay painted on Minecraft's blocks (the oldest go first). Fewer is faster in long fights.",
				0, STAINS.length - 1, stainIndex(), v -> STAIN_NAMES[v], v -> UltracraftConfig.stainCap = STAINS[v]),
			bool("Extra Gore", "Ultracraft's extra blood on every death (sprays and bursts on top of ULTRAKILL's own). Off: just ULTRAKILL's own.",
				UltracraftConfig.extraGore, v -> UltracraftConfig.extraGore = v),
			slider("Terrain Range", "How far around V1 Minecraft's terrain becomes ULTRAKILL's walls and floors. Less is faster for both games; beyond it, shots fly through hills and ULTRAKILL's effects show through them.",
				0, TERRAIN.length - 1, terrainIndex(), v -> TERRAIN[v] + " blocks", v -> UltracraftConfig.terrainRange = TERRAIN[v]));
	}

	private static final String[] FX_NAMES = {"Low", "Medium", "High"};
	private static final int[] STAINS = {0, 500, 1500, 4096, BloodStains.MAX};
	private static final String[] STAIN_NAMES = {"Off", "500", "1,500", "4,096", "All (8,192)"};
	private static final int[] TERRAIN = {64, 96, 128};

	private static int stainIndex() {
		for (int i = STAINS.length - 1; i >= 0; i--) if (UltracraftConfig.stainCap >= STAINS[i]) return i;
		return 0;
	}

	private static int terrainIndex() {
		for (int i = TERRAIN.length - 1; i >= 0; i--) if (UltracraftConfig.terrainRange >= TERRAIN[i]) return i;
		return 0;
	}

	private static void preset(Category c, Runnable apply) {
		apply.run();
		UltracraftConfig.sendOpts();
		UltracraftConfig.save();
		c.reopen();
	}

	private static void music(Category c) {
		c.add(Button.builder(Component.literal("Fight Music: " + UcMusicScreen.current()), b -> Minecraft.getInstance().setScreen(new UcMusicScreen(c.self())))
				.tooltip(Tooltip.create(Component.literal("Which ULTRAKILL song plays in fights: off, a random one each fight, or one from its soundtrack."))).build(),
			null);
		c.add(bool("Boss Themes", "Bosses and arenas bring their own ULTRAKILL song where they have one (V2: Versus, Minos Prime: Order...).",
				UltracraftConfig.bossThemes, v -> UltracraftConfig.bossThemes = v),
			bool("Calm Music", "Between fights the song's calm version plays softly, as in ULTRAKILL's levels. Off: ULTRAKILL's music only plays in fights.",
				UltracraftConfig.calmMusic, v -> {
					UltracraftConfig.calmMusic = v;
					UltracraftConfig.sendMusic();
				}),
			bool("Hush Minecraft's Music", "Minecraft's own music pauses while ULTRAKILL's fight music plays.", UltracraftConfig.hushMcMusic,
				v -> UltracraftConfig.hushMcMusic = v));
		List<OptionInstance<?>> volumes = new ArrayList<>();
		for (UkPref p : UK_PREFS) if (VOLUMES.contains(p.key)) volumes.add(ukOption(p));
		c.add(volumes.toArray(new OptionInstance[0]));
	}

	private static void ukSettings(Category c) {
		List<OptionInstance<?>> uk = new ArrayList<>();
		for (UkPref p : UK_PREFS) if (!VOLUMES.contains(p.key)) uk.add(ukOption(p));
		c.add(uk.toArray(new OptionInstance[0]));
	}

	// ------------------------------------------------------------------ helpers

	private static final int[] RES = {360, 480, 540, 720, 900, 1080, 0};
	private static final String[] RES_NAMES = {"360p", "480p", "540p", "720p", "900p", "1080p", "Full"};

	private static int resIndex() {
		for (int i = 0; i < RES.length; i++) if (RES[i] == UltracraftConfig.v1Height) return i;
		return 3;
	}

	/** The OP Shop changed: every player's Upgrades page hears its new levels. */
	private static void resendUpgrades() {
		var server = Minecraft.getInstance().getSingleplayerServer();
		if (server == null) return;
		server.execute(() -> {
			for (var sp : server.getPlayerList().getPlayers()) UkProgress.get(sp).sendUpgrades();
		});
	}

	static OptionInstance<Boolean> bool(String caption, String tooltip, boolean value, Consumer<Boolean> set) {
		return OptionInstance.createBoolean(caption, OptionInstance.cachedConstantTooltip(Component.literal(tooltip)), value, v -> {
			set.accept(v);
			UltracraftConfig.sendOpts();
		});
	}

	static OptionInstance<Integer> slider(String caption, String tooltip, int min, int max, int value, java.util.function.IntFunction<String> label,
			Consumer<Integer> set) {
		return new OptionInstance<>(caption, OptionInstance.cachedConstantTooltip(Component.literal(tooltip)),
			(cap, v) -> Options.genericValueLabel(cap, Component.literal(label.apply(v))), new OptionInstance.IntRange(min, max), Math.max(min, Math.min(max, value)), v -> {
				set.accept(v);
				UltracraftConfig.sendOpts();
			});
	}

	/** ULTRAKILL's setting as Ultracraft has it, else as ULTRAKILL reported it, else its default ("f:50"). */
	static String current(UkPref p) {
		String v = UltracraftConfig.ukPrefs.get(p.key);
		if (v == null) v = reported.get(p.key);
		if (v == null) v = p.type + ":" + p.def;
		int c = v.indexOf(':');
		return c >= 0 ? v.substring(c + 1) : v;
	}

	private static void setUk(UkPref p, String value) {
		String v = p.type + ":" + value;
		UltracraftConfig.ukPrefs.put(p.key, v);
		UkLink.send("UKPREF " + p.key + " " + v);
	}

	private static OptionInstance<?> ukOption(UkPref p) {
		String now = current(p);
		if (p.type == 'b') {
			return OptionInstance.createBoolean(p.caption, OptionInstance.cachedConstantTooltip(Component.literal(p.tooltip + " (ULTRAKILL's own settings stay as they are.)")),
				Boolean.parseBoolean(now), v -> setUk(p, Boolean.toString(v)));
		}
		// fractions (screen shake, volumes) show as percentages
		boolean percent = p.max == 100 && p.min == 0;
		float f;
		try {
			f = Float.parseFloat(now);
		} catch (NumberFormatException e) {
			f = Float.parseFloat(p.def);
		}
		int value = Math.round(percent ? f * 100f : f);
		java.util.function.IntFunction<String> label = p.key.equals("weaponHoldPosition") ? v -> v == 0 ? "Right" : v == 1 ? "Middle" : "Left"
			: p.key.equals("difficulty") ? v -> UK_DIFFICULTY[Math.max(0, Math.min(4, v))]
			: percent ? v -> v + "%" : Integer::toString;
		return slider(p.caption, p.tooltip + " (ULTRAKILL's own settings stay as they are.)", p.min, p.max, value, label,
			v -> setUk(p, p.type == 'i' ? Integer.toString(v) : percent ? String.format(Locale.ROOT, "%.2f", v / 100f) : String.format(Locale.ROOT, "%.1f", (float) v)));
	}
}
