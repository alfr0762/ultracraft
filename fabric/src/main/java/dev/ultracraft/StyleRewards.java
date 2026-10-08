package dev.ultracraft;

import java.util.Locale;
import net.minecraft.ChatFormatting;
import net.minecraft.network.chat.Component;
import net.minecraft.network.protocol.game.ClientboundSetSubtitleTextPacket;
import net.minecraft.network.protocol.game.ClientboundSetTitleTextPacket;
import net.minecraft.network.protocol.game.ClientboundSetTitlesAnimationPacket;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.sounds.SoundEvents;
import net.minecraft.sounds.SoundSource;
import net.minecraft.util.RandomSource;
import net.minecraft.world.entity.ExperienceOrb;
import net.minecraft.world.entity.item.ItemEntity;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.phys.Vec3;

/**
 * Style pays (the Style Rewards setting). V1's ULTRAKILL reports its style rank as it changes (STYLE rank: 0 D, 1 C,
 * 2 B, 3 A, 4 S, 5 SS, 6 SSS, 7 ULTRAKILL). At S and up every kill (ULTRAKILL's enemies and Minecraft's mobs alike)
 * counts toward a streak, which ends when the rank drops below S: each one there may drop something extra, better the
 * higher the rank (iron and gold at S, emeralds at SS, diamonds and golden apples at SSS, netherite at ULTRAKILL), its
 * experience grows with the streak, and every fifth in a row pays out for sure, with P on top. Server thread.
 */
final class StyleRewards {
	static final int S = 4;
	private static final String[] RANKS = {"D", "C", "B", "A", "S", "SS", "SSS", "ULTRAKILL"};

	private StyleRewards() {}

	/** STYLE rank. */
	static void rank(ServerPlayer sp, int rank) {
		ServerOps.State st = ServerOps.state(sp);
		st.rank = Math.max(0, Math.min(7, rank));
		UkBosses.style(sp, st.rank);
		if (st.rank >= S || st.streak == 0) return;
		if (st.streak >= 5) sp.displayClientMessage(Component.literal("STYLE STREAK OVER: x" + st.streak).withStyle(ChatFormatting.GRAY), true);
		st.streak = 0;
	}

	/** A kill by V1 there: how much its experience is worth (x1 below S), and the loot, now. */
	static float kill(ServerPlayer sp, Vec3 at) {
		if (!UltracraftConfig.styleRewards) return 1f;
		ServerOps.State st = ServerOps.state(sp);
		if (st.rank < S) return 1f;
		st.streak++;
		ServerLevel level = sp.level();
		RandomSource r = sp.getRandom();
		// something extra now and then, more often the higher the rank and the longer the streak
		float chance = 0.12f + 0.06f * (st.rank - S) + Math.min(0.25f, st.streak * 0.01f);
		if (r.nextFloat() < chance) drop(level, at, loot(st.rank, r, false));
		if (st.streak % 5 == 0) milestone(sp, level, at, st, r);
		else sp.displayClientMessage(Component.literal("STYLE STREAK x" + st.streak).withStyle(color(st.rank), ChatFormatting.BOLD), true);
		return 1f + Math.min(2f, st.streak * 0.05f);
	}

	/** Every fifth kill in a row: a sure prize from the top of the rank's loot, and P. */
	private static void milestone(ServerPlayer sp, ServerLevel level, Vec3 at, ServerOps.State st, RandomSource r) {
		drop(level, at, loot(st.rank, r, true));
		int pay = st.streak * 100 * (st.rank - S + 1);
		UkProgress.get(sp).award(pay);
		ExperienceOrb.award(level, at, st.streak * 2);
		Component title = Component.literal("STYLE STREAK x" + st.streak).withStyle(color(st.rank), ChatFormatting.BOLD);
		Component sub = Component.literal(RANKS[st.rank] + "   " + String.format(Locale.ROOT, "+%,d P", pay)).withStyle(ChatFormatting.YELLOW);
		sp.connection.send(new ClientboundSetTitlesAnimationPacket(2, 30, 10));
		sp.connection.send(new ClientboundSetSubtitleTextPacket(sub));
		sp.connection.send(new ClientboundSetTitleTextPacket(title));
		level.playSound(null, at.x, at.y, at.z, SoundEvents.PLAYER_LEVELUP, SoundSource.PLAYERS, 0.8f, 1.2f + 0.1f * (st.rank - S));
		org.slf4j.LoggerFactory.getLogger("ultracraft").info("[style] {} streak x{} at {}: +{} P", sp.getName().getString(), st.streak, RANKS[st.rank], pay);
	}

	private static ChatFormatting color(int rank) {
		return switch (rank) {
			case 4 -> ChatFormatting.GOLD;
			case 5 -> ChatFormatting.RED;
			case 6 -> ChatFormatting.DARK_RED;
			default -> ChatFormatting.LIGHT_PURPLE;
		};
	}

	/** A prize for this rank: mostly from its own tier, sometimes the ones below; sure: its own tier. */
	private static ItemStack loot(int rank, RandomSource r, boolean sure) {
		int top = Math.max(0, Math.min(3, rank - S));
		int tier = sure ? top : Math.max(0, top - (r.nextFloat() < 0.6f ? 0 : r.nextFloat() < 0.6f ? 1 : 2));
		return switch (tier) {
			case 0 -> switch (r.nextInt(5)) {
				case 0 -> new ItemStack(Items.IRON_INGOT, 1 + r.nextInt(3));
				case 1 -> new ItemStack(Items.GOLD_INGOT, 1 + r.nextInt(2));
				case 2 -> new ItemStack(Items.ARROW, 4 + r.nextInt(8));
				case 3 -> new ItemStack(Items.COOKED_BEEF, 2 + r.nextInt(3));
				default -> new ItemStack(Items.COAL, 2 + r.nextInt(4));
			};
			case 1 -> switch (r.nextInt(5)) {
				case 0 -> new ItemStack(Items.EMERALD, 1 + r.nextInt(3));
				case 1 -> new ItemStack(Items.LAPIS_LAZULI, 3 + r.nextInt(6));
				case 2 -> new ItemStack(Items.GOLDEN_CARROT, 2 + r.nextInt(3));
				case 3 -> new ItemStack(Items.EXPERIENCE_BOTTLE, 2 + r.nextInt(4));
				default -> new ItemStack(Items.REDSTONE, 4 + r.nextInt(8));
			};
			case 2 -> switch (r.nextInt(4)) {
				case 0 -> new ItemStack(Items.DIAMOND, 1 + r.nextInt(2));
				case 1 -> new ItemStack(Items.GOLDEN_APPLE);
				case 2 -> new ItemStack(Items.ENDER_PEARL, 1 + r.nextInt(3));
				default -> new ItemStack(Items.OBSIDIAN, 2 + r.nextInt(4));
			};
			default -> switch (r.nextInt(4)) {
				case 0 -> new ItemStack(Items.NETHERITE_SCRAP);
				case 1 -> new ItemStack(Items.DIAMOND, 2 + r.nextInt(3));
				case 2 -> r.nextInt(6) == 0 ? new ItemStack(Items.ENCHANTED_GOLDEN_APPLE) : new ItemStack(Items.GOLDEN_APPLE, 2);
				default -> r.nextInt(10) == 0 ? new ItemStack(Items.TOTEM_OF_UNDYING) : new ItemStack(Items.DIAMOND);
			};
		};
	}

	private static void drop(ServerLevel level, Vec3 at, ItemStack stack) {
		ItemEntity item = new ItemEntity(level, at.x, at.y + 0.3, at.z, stack);
		item.setGlowingTag(true);
		level.addFreshEntity(item);
	}
}
