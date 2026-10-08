package dev.ultracraft;

import com.mojang.serialization.Codec;
import com.mojang.serialization.codecs.RecordCodecBuilder;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.util.datafix.DataFixTypes;
import net.minecraft.world.level.saveddata.SavedData;
import net.minecraft.world.level.saveddata.SavedDataType;

/**
 * What V1 has earned in this world, kept in the world's save (data/ultracraft_progress.dat): its P, the gear bought
 * at the shop with it, the bosses it has beaten, and how long it has played since the last boss. ULTRAKILL never
 * writes any of it to its own save: the bridge asks here instead (GEAR, MONEY) and reports what V1 earns and buys
 * (PEARN, PADD, GEARADD). Server thread only; the HUD reads {@link #shownMoney}.
 */
final class UkProgress extends SavedData {
	private static final Codec<UkProgress> CODEC = RecordCodecBuilder.create(i -> i.group(
		Codec.INT.optionalFieldOf("money", 0).forGetter(p -> p.money),
		Codec.STRING.listOf().optionalFieldOf("gear", List.of()).forGetter(p -> new ArrayList<>(p.gear)),
		Codec.unboundedMap(Codec.STRING, Codec.INT).optionalFieldOf("beaten", Map.of()).forGetter(p -> p.beaten),
		Codec.LONG.optionalFieldOf("bossClock", 0L).forGetter(p -> p.bossClock),
		Codec.LONG.optionalFieldOf("nextBoss", 0L).forGetter(p -> p.nextBoss),
		Codec.LONG.optionalFieldOf("earned", 0L).forGetter(p -> p.earned),
		Codec.unboundedMap(Codec.STRING, Codec.INT).optionalFieldOf("upgrades", Map.of()).forGetter(p -> p.upgrades),
		Codec.unboundedMap(Codec.STRING, Codec.INT).optionalFieldOf("equips", Map.of()).forGetter(p -> p.equips),
		Codec.unboundedMap(Codec.STRING, Codec.LONG.listOf()).optionalFieldOf("bossRecords", Map.of()).forGetter(p -> p.bossRecords.encode())
	).apply(i, UkProgress::new));
	static final SavedDataType<UkProgress> TYPE = type("ultracraft_progress");

	private static SavedDataType<UkProgress> type(String id) {
		return new SavedDataType<>(id, UkProgress::new, CODEC, DataFixTypes.SAVED_DATA_COMMAND_STORAGE);
	}

	/** Whose this is (sends go to that player), and the server they're on. */
	private java.util.UUID owner;
	private MinecraftServer server;

	/** This world's P as last known, for Minecraft's HUD and inventory (any thread). */
	static volatile int shownMoney;
	/** P that just came in, for the HUD's "+P" (any thread); the time it last grew. */
	static volatile int recentGain;
	static volatile long recentAt;

	int money;
	final Set<String> gear = new LinkedHashSet<>();
	final Map<String, Integer> beaten = new HashMap<>();
	/** Ticks V1 has spent playing since the last boss, and the count at which the next one comes. */
	long bossClock, nextBoss;
	/** All the P ever earned here. */
	long earned;
	/** Each upgrade's level (UkUpgrades' keys, "rev.power"...; missing is 1). */
	final Map<String, Integer> upgrades = new HashMap<>();
	/** Which weapons are equipped: weapon.rev0 = 0 off, 1 on, 2 the alternate (missing is on). */
	final Map<String, Integer> equips = new HashMap<>();
	/** Fastest clears for this player, by boss, difficulty and modifier set; absent in older saves. */
	BossRecords bossRecords = new BossRecords();

	UkProgress() {}

	private UkProgress(int money, List<String> gear, Map<String, Integer> beaten, long bossClock, long nextBoss, long earned, Map<String, Integer> upgrades,
		Map<String, Integer> equips, Map<String, List<Long>> bossRecords) {
		this.upgrades.putAll(upgrades);
		this.equips.putAll(equips);
		UkUpgrades.migrate(this.upgrades);
		this.money = money;
		this.gear.addAll(gear);
		this.beaten.putAll(beaten);
		this.bossClock = bossClock;
		this.nextBoss = nextBoss;
		this.earned = earned;
		this.bossRecords = BossRecords.decode(bossRecords);
	}

	/**
	 * A player's own P, gear, upgrades and bosses in this world. The one playing in this process (singleplayer, or the
	 * LAN host) keeps the world's original record; everyone else who joins gets one of their own.
	 */
	static UkProgress get(ServerPlayer sp) {
		MinecraftServer server = sp.level().getServer();
		boolean host = UcNet.isLocal(sp) || server.isSingleplayerOwner(sp.nameAndId());
		UkProgress p = server.overworld().getDataStorage().computeIfAbsent(host ? TYPE : type("ultracraft_progress_" + sp.getUUID()));
		p.owner = sp.getUUID();
		p.server = server;
		return p;
	}

	/** Without a player (the server console): the host's. */
	static UkProgress get(MinecraftServer server) {
		return server.overworld().getDataStorage().computeIfAbsent(TYPE);
	}

	private void sendOwner(String msg) {
		if (owner == null || server == null) return;
		ServerPlayer sp = server.getPlayerList().getPlayer(owner);
		if (sp != null) UcNet.send(sp, msg);
	}

	int bossesBeaten() {
		int n = 0;
		for (int v : beaten.values()) n += v;
		return n;
	}

	/** Style V1 earned in ULTRAKILL (PEARN), point for point (ULTRAKILL's style meter already shows it). */
	void earn(int amount) {
		if (amount <= 0) return;
		money = (int) Math.min(Integer.MAX_VALUE, (long) money + amount);
		earned += amount;
		changed();
	}

	/** A prize from Minecraft's side (a boss, a Cyber Grind wave): it comes up on the HUD, and ULTRAKILL hears the new total. */
	void award(int amount) {
		earn(amount);
		sendOwner("C:PGAIN " + amount);
		sendMoney();
	}

	/** The shop spent P, or something paid it back (PADD, negative to spend). */
	void add(int delta) {
		money = (int) Math.max(0L, Math.min(Integer.MAX_VALUE, (long) money + delta));
		changed();
	}

	void addGear(String g) {
		if (g == null || g.isEmpty() || !gear.add(g)) return;
		changed();
	}

	/** EQUIP key value: the shop equipped, unequipped or switched a weapon to its alternate. */
	void setEquip(String key, int value) {
		if (key == null || !key.startsWith("weapon.") || key.length() > 32 || value < 0 || value > 2) return;
		equips.put(key, value);
		changed();
	}

	int level(String key) {
		UkUpgrades.Track t = UkUpgrades.TRACKS.get(key);
		int max = t != null ? t.max() : 1;
		return Math.max(1, Math.min(max, upgrades.getOrDefault(key, 1)));
	}

	void setLevel(String key, int level) {
		UkUpgrades.Track t = UkUpgrades.TRACKS.get(key);
		if (t == null) return;
		upgrades.put(key, Math.max(1, Math.min(t.max(), level)));
		setDirty();
	}

	/** UPBUY key from the shop's Upgrades page: the next level, if there's one and V1 can pay. */
	boolean buyUpgrade(String key, boolean free) {
		UkUpgrades.Track t = UkUpgrades.TRACKS.get(key);
		if (t == null) return false;
		int lv = level(key);
		int cost = free ? 0 : t.costTo(lv + 1);
		if (lv >= t.max() || money < cost) return false;
		add(-cost);
		setLevel(key, lv + 1);
		return true;
	}

	/** UPGRADES key=level/max/next cost,...: every upgrade's level, how far it goes and what the next level costs. */
	void sendUpgrades() {
		StringBuilder sb = new StringBuilder("UPGRADES ");
		for (UkUpgrades.Track t : UkUpgrades.TRACKS.values()) {
			int lv = level(t.key());
			sb.append(t.key()).append('=').append(lv).append('/').append(t.max()).append('/').append(t.costTo(lv + 1)).append(',');
		}
		sendOwner(sb.substring(0, sb.length() - 1));
	}

	private void changed() {
		setDirty();
	}

	/** On the client: a prize came in (C:PGAIN), for the HUD's "+P". */
	static void gained(int amount) {
		long now = System.currentTimeMillis();
		recentGain = now - recentAt < 6000 ? recentGain + amount : amount;
		recentAt = now;
	}

	/** EQUIPS key=value,... then GEAR money all|gear,...: what V1 has equipped and owns here (all of it with allGear). */
	void send() {
		StringBuilder eq = new StringBuilder("EQUIPS ");
		for (Map.Entry<String, Integer> e : equips.entrySet()) eq.append(e.getKey()).append('=').append(e.getValue()).append(',');
		sendOwner(equips.isEmpty() ? "EQUIPS -" : eq.substring(0, eq.length() - 1));
		sendOwner("GEAR " + money + " " + (UltracraftConfig.allGear ? "all" : gear.isEmpty() ? "-" : String.join(",", gear)));
		sendUpgrades();
	}

	void sendMoney() {
		sendOwner("MONEY " + money);
	}
}
