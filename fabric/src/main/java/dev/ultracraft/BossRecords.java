package dev.ultracraft;

import java.util.Collection;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.TreeSet;

/**
 * Personal fastest boss clears, kept by UkProgress in this Minecraft world. The model has no game dependencies:
 * a clear's time is world ticks, its style is the highest reported rank, and its P is received before the boss prize.
 * These are personal records, not ULTRAKILL level ranks or a competitive leaderboard.
 */
public final class BossRecords {
	public record Result(int ticks, int peakStyle, long earnedP) {
		public Result {
			if (ticks < 0 || peakStyle < 0 || peakStyle > 7 || earnedP < 0) throw new IllegalArgumentException("Invalid boss result");
		}
	}

	private static final String[] RANKS = {"D", "C", "B", "A", "S", "SS", "SSS", "ULTRAKILL"};
	private final Map<String, Result> best = new LinkedHashMap<>();

	public BossRecords() {}

	/** Modifier order and duplicates never split the same fight into different record buckets. */
	public static String key(String boss, int difficulty, Collection<String> modifiers) {
		if (boss == null || !boss.matches("[a-z0-9_]+") || difficulty < 0 || difficulty > 4) throw new IllegalArgumentException("Invalid boss bucket");
		TreeSet<String> ordered = new TreeSet<>();
		for (String modifier : modifiers) {
			if (modifier == null || !modifier.matches("[a-z0-9_]+")) throw new IllegalArgumentException("Invalid boss modifier");
			ordered.add(modifier);
		}
		return boss + "|" + difficulty + "|" + (ordered.isEmpty() ? "-" : String.join(",", ordered));
	}

	/** Only a faster clear replaces the time record; a tie keeps the earlier clear and its associated metrics. */
	public boolean record(String key, Result result) {
		if (!validKey(key)) throw new IllegalArgumentException("Invalid boss bucket");
		Result previous = best.get(key);
		if (previous != null && result.ticks() >= previous.ticks()) return false;
		best.put(key, result);
		return true;
	}

	/** A stable immutable view for the records command, and future terminal screens. */
	public Map<String, Result> entries() {
		return java.util.Collections.unmodifiableMap(new java.util.TreeMap<>(best));
	}

	/** Three integers per bucket: time, highest style rank, earned P. Unknown future shapes are left out. */
	Map<String, List<Long>> encode() {
		Map<String, List<Long>> out = new LinkedHashMap<>();
		entries().forEach((key, value) -> out.put(key, List.of((long) value.ticks(), (long) value.peakStyle(), value.earnedP())));
		return out;
	}

	static BossRecords decode(Map<String, List<Long>> data) {
		BossRecords out = new BossRecords();
		data.forEach((key, values) -> {
			if (!validKey(key) || values == null || values.size() != 3 || values.stream().anyMatch(java.util.Objects::isNull)) return;
			long ticks = values.get(0), rank = values.get(1), earned = values.get(2);
			if (ticks < 0 || ticks > Integer.MAX_VALUE || rank < 0 || rank > 7 || earned < 0) return;
			// Canonicalize older/modifier-order variants on reading as well as when recording.
			String[] parts = key.split("\\|", -1);
			out.record(key(parts[0], Integer.parseInt(parts[1]), parts[2].equals("-") ? List.of() : List.of(parts[2].split(","))),
				new Result((int) ticks, (int) rank, earned));
		});
		return out;
	}

	private static boolean validKey(String key) {
		return key != null && key.matches("[a-z0-9_]+\\|[0-4]\\|(?:-|[a-z0-9_]+(?:,[a-z0-9_]+)*)");
	}

	public static String time(int ticks) {
		if (ticks < 0) throw new IllegalArgumentException("Negative time");
		return String.format(Locale.ROOT, "%d:%02d.%02d", ticks / 1200, ticks / 20 % 60, ticks % 20 * 5);
	}

	/** World-tick duration, independently of the boss controller's ten-tick scheduling; safe on reset/overflow. */
	public static int elapsedTicks(long startedAt, long now) {
		if (now <= startedAt) return 0;
		long elapsed = now - startedAt;
		return elapsed < 0 || elapsed > Integer.MAX_VALUE ? Integer.MAX_VALUE : (int) elapsed;
	}

	public static String rank(int rank) {
		return RANKS[Math.max(0, Math.min(7, rank))];
	}
}
