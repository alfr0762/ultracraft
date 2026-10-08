package dev.ultracraft;

import java.util.HashMap;
import java.util.List;
import java.util.Map;

/** Pure model checks: run with javac/java, no Minecraft install, launcher or game assets needed. */
public final class BossRecordsTest {
	private static int checks;

	public static void main(String[] args) {
		String ordinary = BossRecords.key("v2", 2, List.of());
		String traits = BossRecords.key("v2", 2, List.of("twin", "radiant", "twin"));
		equal("v2|2|-", ordinary);
		equal("v2|2|radiant,twin", traits);
		equal(traits, BossRecords.key("v2", 2, List.of("radiant", "twin")));
		check(!traits.equals(BossRecords.key("v2", 1, List.of("radiant", "twin"))), "difficulty buckets differ");
		equal("0:00.00", BossRecords.time(0));
		equal("0:00.05", BossRecords.time(1));
		equal("0:59.95", BossRecords.time(1199));
		equal("1:00.00", BossRecords.time(1200));
		equal("20:00.00", BossRecords.time(24000));
		equal(0, BossRecords.elapsedTicks(100L, 100L));
		equal(0, BossRecords.elapsedTicks(100L, 99L));
		equal(1, BossRecords.elapsedTicks(100L, 101L));
		equal(1234, BossRecords.elapsedTicks(100L, 1334L));
		equal(Integer.MAX_VALUE, BossRecords.elapsedTicks(0L, Long.MAX_VALUE));
		equal(Integer.MAX_VALUE, BossRecords.elapsedTicks(Long.MIN_VALUE, Long.MAX_VALUE));

		BossRecords records = new BossRecords();
		BossRecords.Result first = new BossRecords.Result(1200, 4, 500L);
		check(records.record(ordinary, first), "first clear creates a record");
		check(!records.record(ordinary, new BossRecords.Result(1201, 7, 99999L)), "more style never replaces a faster time");
		check(!records.record(ordinary, new BossRecords.Result(1200, 7, 99999L)), "tie keeps earlier clear's metrics");
		equal(first, records.entries().get(ordinary));
		BossRecords.Result faster = new BossRecords.Result(1199, 2, 77L);
		check(records.record(ordinary, faster), "faster clear replaces previous");
		BossRecords.Result rich = new BossRecords.Result(2000, 7, 3_000_000_000L);
		check(records.record(traits, rich), "modifier bucket is independent");
		equal(faster, records.entries().get(ordinary));
		equal(rich, records.entries().get(traits));
		expect(UnsupportedOperationException.class, () -> records.entries().clear());

		// The save codec writes this exact map of long lists. Round trips retain P beyond a 32-bit integer.
		Map<String, List<Long>> encoded = records.encode();
		equal(records.entries(), BossRecords.decode(encoded).entries());
		equal(List.of(2000L, 7L, 3_000_000_000L), encoded.get(traits));
		encoded.clear();
		equal(2, records.entries().size());
		equal(Map.of(), BossRecords.decode(Map.of()).entries()); // old saves have no optional field

		Map<String, List<Long>> damaged = new HashMap<>();
		damaged.put(ordinary, List.of(100L, 3L, 30L));
		damaged.put("invalid", List.of(1L, 1L, 1L));
		damaged.put("v2|4|-", List.of(-1L, 4L, 1L));
		damaged.put("v2|1|-", List.of((long) Integer.MAX_VALUE + 1L, 4L, 1L));
		damaged.put("v2|3|-", List.of(10L, 8L, 1L));
		damaged.put("v2|0|-", List.of(10L, 4L, -1L));
		damaged.put("gabriel|2|-", List.of(10L, 4L));
		damaged.put("v2|2|twin,radiant", List.of(100L, 7L, 99L));
		damaged.put("v2|2|radiant,twin", List.of(101L, 7L, 100L));
		BossRecords restored = BossRecords.decode(damaged);
		equal(2, restored.entries().size());
		equal(new BossRecords.Result(100, 7, 99L), restored.entries().get(traits));
		equal(new BossRecords.Result(100, 3, 30L), restored.entries().get(ordinary));
		expect(IllegalArgumentException.class, () -> new BossRecords.Result(-1, 0, 0));
		expect(IllegalArgumentException.class, () -> new BossRecords.Result(0, 8, 0));
		expect(IllegalArgumentException.class, () -> new BossRecords.Result(0, 0, -1));
		expect(IllegalArgumentException.class, () -> BossRecords.key("v2|2", 2, List.of()));
		expect(IllegalArgumentException.class, () -> BossRecords.key("v2", 5, List.of()));
		expect(IllegalArgumentException.class, () -> BossRecords.time(-1));
		System.out.println("BossRecordsTest: " + checks + " checks passed.");
	}

	private static void equal(Object expected, Object actual) {
		check(expected.equals(actual), "expected " + expected + ", got " + actual);
	}

	private static void check(boolean condition, String message) {
		checks++;
		if (!condition) throw new AssertionError(message);
	}

	private static void expect(Class<? extends Throwable> kind, Runnable action) {
		checks++;
		try { action.run(); }
		catch (Throwable error) {
			if (kind.isInstance(error)) return;
			throw new AssertionError("expected " + kind.getName(), error);
		}
		throw new AssertionError("expected " + kind.getName());
	}
}
