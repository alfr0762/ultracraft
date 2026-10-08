package dev.ultracraft;

import com.mojang.brigadier.CommandDispatcher;
import com.mojang.brigadier.arguments.IntegerArgumentType;
import com.mojang.brigadier.arguments.StringArgumentType;
import com.mojang.brigadier.context.CommandContext;
import com.mojang.brigadier.exceptions.CommandSyntaxException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Locale;
import net.minecraft.ChatFormatting;
import net.minecraft.commands.CommandSourceStack;
import net.minecraft.commands.Commands;
import net.minecraft.commands.SharedSuggestionProvider;
import net.minecraft.commands.arguments.EntityArgument;
import net.minecraft.network.chat.Component;
import net.minecraft.server.level.ServerPlayer;

/**
 * /uc (or /ultracraft): try everything Ultracraft adds without earning it first. Works in any world, cheats on or not.
 * <pre>
 * /uc p                         this world's P
 * /uc p add|take|set &lt;amount&gt;     (these and the others that hand things out: the host or operators only)
 * /uc p give &lt;player&gt; &lt;amount&gt; your P to another player
 * /uc weapons all|none|list     every weapon, variant, alternate and arm / back to the Piercer and Feedbacker
 * /uc weapons give|take &lt;gear&gt;  one piece (rev1, sho0, shoalt, arm1...)
 * /uc upgrades max|reset|list
 * /uc upgrades set &lt;upgrade&gt; &lt;level&gt;  (rev.power, arm0.reflex, rock.payload...)
 * /uc settings                  the settings screen
 * /uc records [page]            your fastest boss clears in this world
 * /uc boss call &lt;boss|next&gt; [seconds of warning]
 * /uc boss kill|leave|list|status
 * /uc boss timer &lt;minutes&gt;      the next boss after that much play
 * /uc bosses on|off
 * /uc grind start|join|arena &lt;n&gt;|stop
 * /uc duel &lt;player&gt;|accept|decline|forfeit   PvP with another V1
 * </pre>
 */
final class UkCommands {
	/** Everything the shop sells that a world can own. */
	static final List<String> GEAR = List.of("rev0", "rev1", "rev2", "revalt", "sho0", "sho1", "sho2", "shoalt", "nai0", "nai1", "nai2", "naialt",
		"rai0", "rai1", "rai2", "rock0", "rock1", "rock2", "arm1", "arm2", "color0", "color1", "color2", "color3", "color4");

	private UkCommands() {}

	static void register(CommandDispatcher<CommandSourceStack> d) {
		var root = Commands.literal("uc")
			.executes(c -> help(c))
			.then(Commands.literal("p")
				.executes(c -> say(c, "This world has " + money(c) + "."))
				.then(Commands.literal("add").requires(UkCommands::cheats).then(Commands.argument("amount", IntegerArgumentType.integer(0)).executes(c -> money(c, IntegerArgumentType.getInteger(c, "amount"), false))))
				.then(Commands.literal("take").requires(UkCommands::cheats).then(Commands.argument("amount", IntegerArgumentType.integer(0)).executes(c -> money(c, -IntegerArgumentType.getInteger(c, "amount"), false))))
				.then(Commands.literal("set").requires(UkCommands::cheats).then(Commands.argument("amount", IntegerArgumentType.integer(0)).executes(c -> money(c, IntegerArgumentType.getInteger(c, "amount"), true))))
				// P from you to another player (theirs is theirs: each player has their own)
				.then(Commands.literal("give").then(Commands.argument("player", EntityArgument.player())
					.then(Commands.argument("amount", IntegerArgumentType.integer(1)).executes(c -> giveP(c, EntityArgument.getPlayer(c, "player"), IntegerArgumentType.getInteger(c, "amount")))))))
			.then(Commands.literal("weapons")
				.then(Commands.literal("all").requires(UkCommands::cheats).executes(c -> {
					UkProgress p = progress(c);
					p.gear.addAll(GEAR);
					p.setDirty();
					p.send();
					return say(c, "Every weapon, variant, alternate, arm and colour is yours.");
				}))
				.then(Commands.literal("none").requires(UkCommands::cheats).executes(c -> {
					UkProgress p = progress(c);
					p.gear.clear();
					p.setDirty();
					if (UltracraftConfig.allGear) {
						UltracraftConfig.allGear = false;
						UltracraftConfig.save();
					}
					p.send();
					return say(c, "Back to the Piercer revolver and the Feedbacker.");
				}))
				.then(Commands.literal("list").executes(c -> {
					UkProgress p = progress(c);
					List<String> names = new ArrayList<>();
					for (String g : p.gear) names.add(GearNames.of(g));
					return say(c, "Owned: Piercer Revolver, Feedbacker" + (names.isEmpty() ? "" : ", " + String.join(", ", names)) + (UltracraftConfig.allGear ? " (allGear is on: everything)" : ""));
				}))
				.then(Commands.literal("give").requires(UkCommands::cheats).then(Commands.argument("gear", StringArgumentType.word())
					.suggests((c, b) -> SharedSuggestionProvider.suggest(GEAR, b))
					.executes(c -> gear(c, StringArgumentType.getString(c, "gear"), true))))
				.then(Commands.literal("take").requires(UkCommands::cheats).then(Commands.argument("gear", StringArgumentType.word())
					.suggests((c, b) -> SharedSuggestionProvider.suggest(GEAR, b))
					.executes(c -> gear(c, StringArgumentType.getString(c, "gear"), false)))))
			.then(Commands.literal("upgrades")
				.then(Commands.literal("max").requires(UkCommands::cheats).executes(c -> upgradeAll(c, true)))
				.then(Commands.literal("reset").requires(UkCommands::cheats).executes(c -> upgradeAll(c, false)))
				.then(Commands.literal("list").executes(c -> {
					UkProgress p = progress(c);
					StringBuilder sb = new StringBuilder("Upgrades:");
					for (var g : UkUpgrades.GROUPS.entrySet()) {
						sb.append("\n ").append(g.getValue()).append(':');
						for (UkUpgrades.Track t : UkUpgrades.TRACKS.values()) {
							if (t.group().equals(g.getKey())) sb.append(' ').append(t.name()).append(' ').append(p.level(t.key())).append('/').append(t.max()).append(',');
						}
						sb.setLength(sb.length() - 1);
					}
					return say(c, sb.toString());
				}))
				.then(Commands.literal("set").requires(UkCommands::cheats).then(Commands.argument("upgrade", StringArgumentType.word())
					.suggests((c, b) -> SharedSuggestionProvider.suggest(UkUpgrades.keys(), b))
					.then(Commands.argument("level", IntegerArgumentType.integer(1, 20)).executes(c -> {
						String key = StringArgumentType.getString(c, "upgrade");
						if (!UkUpgrades.TRACKS.containsKey(key)) return fail(c, "Unknown upgrade. Try: " + String.join(", ", UkUpgrades.keys()));
						UkProgress p = progress(c);
						p.setLevel(key, IntegerArgumentType.getInteger(c, "level"));
						p.sendUpgrades();
						return say(c, UkUpgrades.name(key) + " is level " + p.level(key) + " of " + UkUpgrades.TRACKS.get(key).max() + ".");
					})))))
			.then(Commands.literal("records")
				.executes(c -> records(c, 1))
				.then(Commands.argument("page", IntegerArgumentType.integer(1)).executes(c -> records(c, IntegerArgumentType.getInteger(c, "page")))))
			.then(Commands.literal("settings").executes(c -> {
				// the settings screen is Minecraft's (client side): open it on the next frame, in the game of whoever asked
				// (a player on someone else's world: their own, not the host's)
				ServerPlayer asker = c.getSource().getPlayer();
				if (asker != null && !UcNet.isLocal(asker)) {
					UcNet.send(asker, "C:SETTINGS");
					return 1;
				}
				net.minecraft.client.Minecraft mc = net.minecraft.client.Minecraft.getInstance();
				mc.execute(() -> mc.setScreen(new UcSettingsScreen(null)));
				return 1;
			}))
			.then(Commands.literal("boss")
				.then(Commands.literal("call").requires(UkCommands::cheats).then(Commands.argument("boss", StringArgumentType.word())
					.suggests((c, b) -> {
						List<String> keys = new ArrayList<>(List.of("next"));
						for (UkBosses.Boss boss : UkBosses.ROSTER) keys.add(boss.key());
						return SharedSuggestionProvider.suggest(keys, b);
					})
					.executes(c -> callBoss(c, StringArgumentType.getString(c, "boss"), UltracraftConfig.bossWarnSeconds))
					.then(Commands.argument("seconds", IntegerArgumentType.integer(3, 600))
						.executes(c -> callBoss(c, StringArgumentType.getString(c, "boss"), IntegerArgumentType.getInteger(c, "seconds")))
						// modifiers and difficulty: radiant,volatile,nightmare (or none)
						.then(Commands.argument("mods", StringArgumentType.word())
							.suggests((c, b) -> {
								List<String> keys = new ArrayList<>(List.of("none", "easy", "medium", "hard", "nightmare", "ukmd"));
								for (UkBosses.Mod m : UkBosses.MODS) keys.add(m.key());
								return SharedSuggestionProvider.suggest(keys, b);
							})
							.executes(c -> callBoss(c, StringArgumentType.getString(c, "boss"), IntegerArgumentType.getInteger(c, "seconds"),
								StringArgumentType.getString(c, "mods")))))))
				.then(Commands.literal("kill").requires(UkCommands::cheats).executes(c -> {
					int id = UkBosses.fightId();
					if (id == 0) return fail(c, "No boss is fighting you.");
					ServerPlayer t = UkBosses.target(c.getSource().getServer());
					if (t != null) UcNet.send(t, "BOSSHURT " + id + " 100000");
					return say(c, "Boss struck down.");
				}))
				.then(Commands.literal("leave").executes(c -> {
					if (!UkBosses.busy()) return fail(c, "No boss is coming or here.");
					// (a boss coming for someone else is theirs to send away, or the host's)
					ServerPlayer t = UkBosses.target(c.getSource().getServer());
					if (t != null && t != c.getSource().getPlayer() && !cheats(c.getSource())) return fail(c, "That boss is after " + t.getName().getString() + ".");
					UkBosses.stop(player(c), "sent away");
					return 1;
				}))
				.then(Commands.literal("list").executes(c -> {
					UkProgress p = progress(c);
					StringBuilder sb = new StringBuilder("Bosses (tier, beaten):");
					for (UkBosses.Boss b : UkBosses.ROSTER) {
						sb.append("\n ").append(b.key()).append(": ").append(b.name()).append(", tier ").append(b.tier()).append(", ")
							.append(String.format(Locale.ROOT, "%,d P", b.reward())).append(", beaten ").append(p.beaten.getOrDefault(b.key(), 0));
					}
					return say(c, sb.toString());
				}))
				.then(Commands.literal("status").executes(c -> {
					UkProgress p = progress(c);
					long left = Math.max(0, p.nextBoss - p.bossClock);
					return say(c, String.format(Locale.ROOT, "Bosses %s. Now: %s. Next one after about %d more minutes of play. Beaten: %d.",
						UltracraftConfig.bosses ? "on" : "off", UkBosses.state(), (left + 1199) / 1200, p.bossesBeaten()));
				}))
				.then(Commands.literal("timer").requires(UkCommands::cheats).then(Commands.argument("minutes", IntegerArgumentType.integer(0, 600)).executes(c -> {
					UkProgress p = progress(c);
					p.nextBoss = p.bossClock + IntegerArgumentType.getInteger(c, "minutes") * 1200L;
					p.setDirty();
					return say(c, "The next boss comes after " + IntegerArgumentType.getInteger(c, "minutes") + " minutes of play.");
				}))))
			.then(Commands.literal("bosses").requires(UkCommands::cheats)
				.then(Commands.literal("on").executes(c -> bosses(c, true)))
				.then(Commands.literal("off").executes(c -> bosses(c, false))))
			// PvP: challenge another V1 (Duels), answer a challenge, or give up
			.then(Commands.literal("duel")
				.then(Commands.literal("accept").executes(c -> duelAnswer(c, true)))
				.then(Commands.literal("decline").executes(c -> duelAnswer(c, false)))
				.then(Commands.literal("forfeit").executes(c -> Duels.forfeit(player(c)) ? 1 : fail(c, "You're not in a duel.")))
				.then(Commands.argument("player", EntityArgument.player()).executes(c -> {
					ServerPlayer to = EntityArgument.getPlayer(c, "player");
					String why = Duels.challenge(player(c), to);
					return why != null ? fail(c, why) : say(c, "You challenged " + to.getName().getString() + " to a duel.");
				})))
			// the Cyber Grind's arenas: a run from a random one or straight to one (its waves start as usual), or out of them
			.then(Commands.literal("grind")
				// into another V1's run (in the Grind's arenas), from wherever you are
				.then(Commands.literal("join").executes(c -> {
					ServerPlayer sp = player(c);
					if (!CyberGrind.running) return fail(c, "The Cyber Grind isn't running: /uc grind start, or start it from a shop.");
					if (CyberGrind.inRun(sp)) return fail(c, "You're in it already.");
					if (!CyberGrind.inArenas()) return fail(c, "That run is round a shop: go there and fight.");
					CyberGrind.join(sp);
					return 1;
				}))
				.then(Commands.literal("leave").executes(c -> {
					if (!CyberGrind.leaveRun(player(c))) return fail(c, "You're not in a run in the Cyber Grind's arenas (round a shop, just walk away).");
					return 1;
				}))
				.then(Commands.literal("start").executes(c -> {
					ServerPlayer sp = player(c);
					if (CyberGrind.running) return fail(c, "The Cyber Grind is already running: /uc grind join to go in with them.");
					if (!CyberGrind.startArenas(sp, -1)) return fail(c, "Become V1 first.");
					return say(c, "Into the Cyber Grind.");
				}))
				.then(Commands.literal("arena").then(Commands.argument("number", IntegerArgumentType.integer(1, GrindArenas.ARENAS.size())).executes(c -> {
					ServerPlayer sp = player(c);
					if (CyberGrind.running) return fail(c, "The Cyber Grind is already running: /uc grind stop first.");
					if (!CyberGrind.startArenas(sp, IntegerArgumentType.getInteger(c, "number") - 1)) return fail(c, "Become V1 first.");
					return say(c, "Into the Cyber Grind's arena " + IntegerArgumentType.getInteger(c, "number") + ".");
				})))
				.then(Commands.literal("stop").executes(c -> {
					if (!CyberGrind.running) return fail(c, "The Cyber Grind isn't running.");
					if (!CyberGrind.isRunner(player(c)) && !cheats(c.getSource())) return fail(c, "It's not your run: /uc grind leave to go back.");
					CyberGrind.stop(player(c), "stopped");
					return say(c, "The Cyber Grind stopped.");
				})));
		var node = d.register(root);
		d.register(Commands.literal("ultracraft").executes(c -> help(c)).redirect(node));
	}

	/** Cheats: the host (or singleplayer), an operator, or the console. */
	private static boolean cheats(CommandSourceStack src) {
		ServerPlayer sp = src.getPlayer();
		return sp == null || ServerOps.cheatsAllowed(sp);
	}

	/** /uc p give player amount: P from you to them. */
	private static int giveP(CommandContext<CommandSourceStack> c, ServerPlayer to, int amount) throws CommandSyntaxException {
		ServerPlayer from = player(c);
		if (to == from) return fail(c, "That's you.");
		UkProgress mine = UkProgress.get(from), theirs = UkProgress.get(to);
		if (mine == theirs) return fail(c, "You two share your P already.");
		if (mine.money < amount) return fail(c, String.format(Locale.ROOT, "You only have %,d P.", mine.money));
		mine.add(-amount);
		mine.sendMoney();
		theirs.award(amount);
		to.sendSystemMessage(Component.literal(String.format(Locale.ROOT, "[Ultracraft] %s gave you %,d P.", from.getName().getString(), amount)).withStyle(ChatFormatting.GOLD));
		return say(c, String.format(Locale.ROOT, "You gave %s %,d P. You have %,d P left.", to.getName().getString(), amount, mine.money));
	}

	private static int duelAnswer(CommandContext<CommandSourceStack> c, boolean yes) throws CommandSyntaxException {
		String why = Duels.answer(player(c), yes);
		return why != null ? fail(c, why) : yes ? 1 : say(c, "Duel declined.");
	}

	/** Every player can inspect their own records; the command never reads another player's progress or changes it. */
	private static int records(CommandContext<CommandSourceStack> c, int page) throws CommandSyntaxException {
		var entries = new ArrayList<>(UkProgress.get(player(c)).bossRecords.entries().entrySet());
		if (entries.isEmpty()) {
			c.getSource().sendSuccess(() -> Component.translatable("ultracraft.records.empty").withStyle(ChatFormatting.GRAY), false);
			return 1;
		}
		int perPage = 8, pages = (entries.size() + perPage - 1) / perPage;
		if (page > pages) {
			c.getSource().sendFailure(Component.translatable("ultracraft.records.no_page", pages));
			return 0;
		}
		c.getSource().sendSuccess(() -> Component.translatable("ultracraft.records.header", entries.size(), page, pages).withStyle(ChatFormatting.GOLD), false);
		for (int n = (page - 1) * perPage; n < Math.min(page * perPage, entries.size()); n++) {
			var entry = entries.get(n);
			BossRecords.Result result = entry.getValue();
			Component text = Component.translatable("ultracraft.records.entry", UkBosses.recordLabel(entry.getKey()), BossRecords.time(result.ticks()),
				BossRecords.rank(result.peakStyle()), String.format(Locale.ROOT, "%,d", result.earnedP())).withStyle(ChatFormatting.GRAY);
			c.getSource().sendSuccess(() -> text, false);
		}
		if (page < pages) c.getSource().sendSuccess(() -> Component.translatable("ultracraft.records.next_page", page + 1).withStyle(ChatFormatting.YELLOW), false);
		return 1;
	}

	private static int help(CommandContext<CommandSourceStack> c) {
		return say(c, "/uc p [give <player> <amount>|add|take|set <amount>]\n/uc weapons all|none|list|give <gear>|take <gear>\n/uc upgrades max|reset|list|set <upgrade> <level>\n/uc settings\n/uc records [page]"
			+ "\n/uc boss call <boss|next> [seconds] [mods,difficulty]\n/uc boss kill|leave|list|status|timer <minutes>\n/uc bosses on|off\n/uc grind start|join|leave|arena <1-50>|stop\n/uc duel <player>|accept|decline|forfeit");
	}

	private static ServerPlayer player(CommandContext<CommandSourceStack> c) throws CommandSyntaxException {
		return c.getSource().getPlayerOrException();
	}

	/** Whoever runs the command: their own P, gear and upgrades (the host's from the console). */
	private static UkProgress progress(CommandContext<CommandSourceStack> c) {
		ServerPlayer sp = c.getSource().getPlayer();
		return sp != null ? UkProgress.get(sp) : UkProgress.get(c.getSource().getServer());
	}

	private static String money(CommandContext<CommandSourceStack> c) {
		return String.format(Locale.ROOT, "%,d P", progress(c).money);
	}

	private static int money(CommandContext<CommandSourceStack> c, int amount, boolean set) {
		UkProgress p = progress(c);
		if (set) p.add(amount - p.money);
		else if (amount > 0) p.award(amount);
		else p.add(amount);
		p.sendMoney();
		return say(c, "This world has " + money(c) + ".");
	}

	private static int gear(CommandContext<CommandSourceStack> c, String g, boolean give) {
		if (!GEAR.contains(g)) return fail(c, "Unknown gear. Try: " + String.join(", ", GEAR));
		UkProgress p = progress(c);
		if (give) p.gear.add(g);
		else p.gear.remove(g);
		p.setDirty();
		p.send();
		return say(c, (give ? "Given: " : "Taken: ") + GearNames.of(g) + ".");
	}

	private static int upgradeAll(CommandContext<CommandSourceStack> c, boolean max) {
		UkProgress p = progress(c);
		for (UkUpgrades.Track t : UkUpgrades.TRACKS.values()) p.setLevel(t.key(), max ? t.max() : 1);
		p.sendUpgrades();
		return say(c, max ? "Every upgrade is maxed: " + (UltracraftConfig.opShop ? "1500%" : "300%") + " power and every special upgrade." : "Every upgrade is back to level 1.");
	}

	private static int callBoss(CommandContext<CommandSourceStack> c, String key, int seconds) throws CommandSyntaxException {
		return callBoss(c, key, seconds, null);
	}

	private static int callBoss(CommandContext<CommandSourceStack> c, String key, int seconds, String mods) throws CommandSyntaxException {
		if (!UcNet.isV1(player(c))) return fail(c, "Become V1 first (F8).");
		if (UkBosses.busy()) return fail(c, "A boss is already coming or here (/uc boss leave).");
		if (!key.equals("next") && UkBosses.byKey(key) == null) return fail(c, "Unknown boss. See /uc boss list.");
		Ultracraft.lastInputAt = System.currentTimeMillis();
		UkBosses.force(player(c), key, seconds, mods);
		return 1;
	}

	private static int bosses(CommandContext<CommandSourceStack> c, boolean on) {
		UltracraftConfig.bosses = on;
		UltracraftConfig.save();
		return say(c, on ? "ULTRAKILL's bosses will come for you." : "No more bosses (until /uc bosses on).");
	}

	private static int say(CommandContext<CommandSourceStack> c, String text) {
		c.getSource().sendSuccess(() -> Component.literal(text).withStyle(ChatFormatting.GOLD), false);
		return 1;
	}

	private static int fail(CommandContext<CommandSourceStack> c, String text) {
		c.getSource().sendFailure(Component.literal(text));
		return 0;
	}
}
