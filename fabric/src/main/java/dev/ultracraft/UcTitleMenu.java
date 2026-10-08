package dev.ultracraft;

import java.util.ArrayList;
import java.util.Map;
import java.util.WeakHashMap;
import net.fabricmc.fabric.api.client.screen.v1.ScreenEvents;
import net.fabricmc.fabric.api.client.screen.v1.Screens;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.Font;
import net.minecraft.client.gui.GuiGraphics;
import net.minecraft.client.gui.components.AbstractWidget;
import net.minecraft.client.gui.components.Button;
import net.minecraft.client.gui.screens.Screen;
import net.minecraft.client.gui.screens.TitleScreen;
import net.minecraft.network.chat.Component;
import net.minecraft.network.chat.contents.TranslatableContents;
import net.minecraft.resources.Identifier;

/** A visual layer over the real title menu: vanilla still owns each action, lock, tooltip and focus target. */
public final class UcTitleMenu {
	private static final Identifier BACKGROUND = Identifier.fromNamespaceAndPath("ultracraft", "textures/gui/terminal_background.png");
	private record Decoration(String index, boolean primary) {}
	private static final Map<AbstractWidget, Decoration> DECORATED = new WeakHashMap<>();
	private static AbstractWidget realmsButton;

	private UcTitleMenu() {}

	public static boolean enabled() {
		return UltracraftConfig.terminalMenu && !Minecraft.getInstance().isDemo();
	}

	static void register() {
		ScreenEvents.AFTER_INIT.register((mc, screen, width, height) -> {
			if (!(screen instanceof TitleScreen) || !enabled()) return;
			MenuLayout layout = MenuLayout.forSize(width, height);
			realmsButton = null;
			AbstractWidget singleplayer = null;
			for (AbstractWidget widget : new ArrayList<>(Screens.getButtons(screen))) {
				String key = key(widget);
				switch (key) {
					case "menu.singleplayer" -> { place(widget, layout, 0, "01", true); singleplayer = widget; }
					case "menu.multiplayer" -> place(widget, layout, 1, "02", false);
					case "menu.online" -> { place(widget, layout, 2, "03", false); realmsButton = widget; }
					case "menu.options" -> {
						widget.setRectangle((layout.width() - 4) / 2, layout.rowHeight(), layout.x(), layout.rowY(4));
						decorate(widget, "", false);
					}
					case "menu.quit" -> place(widget, layout, 5, "05", false);
					case "options.language" -> widget.setPosition(layout.x(), height - 32);
					case "options.accessibility", "accessibility.onboarding.accessibility.button" -> widget.setPosition(layout.x() + 24, height - 32);
					default -> {
						// The existing settings registration runs before this one; retain its actual callback.
						if (widget.getMessage().getString().equals("Ultracraft...")) {
							widget.setMessage(Component.translatable("ultracraft.menu.settings"));
							int half = (layout.width() - 4) / 2;
							widget.setRectangle(layout.width() - half - 4, layout.rowHeight(), layout.x() + half + 4, layout.rowY(4));
							decorate(widget, "", false);
						}
					}
				}
			}
			Button manual = Button.builder(Component.translatable("ultracraft.menu.manual"), b -> mc.setScreen(new UcFieldManualScreen(screen)))
				.bounds(layout.x(), layout.rowY(3), layout.width(), layout.rowHeight()).build();
			decorate(manual, "04", false);
			Screens.getButtons(screen).add(manual);
			if (singleplayer != null) screen.setFocused(singleplayer);
		});
	}

	private static String key(AbstractWidget widget) {
		return widget.getMessage().getContents() instanceof TranslatableContents contents ? contents.getKey() : "";
	}

	private static void place(AbstractWidget widget, MenuLayout layout, int row, String index, boolean primary) {
		widget.setRectangle(layout.width(), row == 0 ? layout.primaryHeight() : layout.rowHeight(), layout.x(), layout.rowY(row));
		decorate(widget, index, primary);
	}

	public static void decorate(AbstractWidget widget, String index, boolean primary) {
		DECORATED.put(widget, new Decoration(index, primary));
	}

	/** A static, full-quality texture. Cover cropping preserves its proportions on narrow and ultrawide windows. */
	public static void paintBackground(GuiGraphics graphics, int width, int height) {
		float artAspect = 1672f / 941f;
		float viewport = width / (float) Math.max(1, height);
		float u = viewport < artAspect ? viewport / artAspect : 1f;
		float v = viewport > artAspect ? artAspect / viewport : 1f;
		graphics.blit(BACKGROUND, 0, 0, width, height, (1 - u) / 2, (1 + u) / 2, (1 - v) / 2, (1 + v) / 2);
		graphics.fillGradient(0, 0, width, height, 0x18000000, 0x65000000);
	}

	public static void paintTitle(GuiGraphics graphics, int width, int height) {
		paintBackground(graphics, width, height);
		Font font = Minecraft.getInstance().font;
		MenuLayout layout = MenuLayout.forSize(width, height);
		int x = layout.x(), y = layout.titleY();
		graphics.fill(x, y, x + 22, y + 2, 0xFFFF353A);
		graphics.drawString(font, Component.translatable("ultracraft.menu.terminal"), x + 29, y - 3, 0xFFB7AAA6, false);
		graphics.pose().pushMatrix();
		graphics.pose().translate(x, y + 12);
		graphics.pose().scale(2f, 2f);
		graphics.drawString(font, "ULTRACRAFT", 0, 0, 0xFFF6EEE6, false);
		graphics.pose().popMatrix();
		graphics.drawString(font, Component.translatable("ultracraft.menu.subtitle"), x, y + 35, 0xFFB5AAA5, false);
		if (width >= 500) {
			Component state = Component.translatable(UkLink.connected ? "ultracraft.menu.connected" : "ultracraft.menu.standby");
			int sx = width - font.width(state) - 16;
			graphics.fill(sx - 10, 19, sx - 6, 23, UkLink.connected ? 0xFF8AE1B0 : 0xFFD64B47);
			graphics.drawString(font, state, sx, 17, 0xFFDDCDC4, false);
		}
	}

	/** Called from Button.Plain only. Tooltip, narration, click handling and cursor remain in vanilla. */
	public static boolean paintButton(AbstractWidget widget, GuiGraphics graphics) {
		Decoration decoration = DECORATED.get(widget);
		Screen screen = Minecraft.getInstance().screen;
		if (decoration == null || !(screen instanceof UcFieldManualScreen || screen instanceof TitleScreen && enabled())) return false;
		boolean highlighted = widget.active && widget.isHoveredOrFocused();
		int x = widget.getX(), y = widget.getY(), w = widget.getWidth(), h = widget.getHeight();
		boolean primary = decoration.primary;
		int fill = !widget.active ? 0xB516171B : primary ? (highlighted ? 0xFFF04447 : 0xE7C62C33) : (highlighted ? 0xEA392124 : 0xC5151419);
		graphics.fill(x, y, x + w, y + h, fill);
		graphics.renderOutline(x, y, w, h, highlighted ? 0xFFFFA39B : primary ? 0xFFEA5555 : 0xFF524047);
		if (widget.isFocused()) graphics.renderOutline(x + 2, y + 2, w - 4, h - 4, 0xFFFFD4C1);
		Font font = Minecraft.getInstance().font;
		int textY = y + (h - 8) / 2;
		int inset = decoration.index.isEmpty() ? 8 : 28;
		int color = widget.active ? 0xFFF8EEE6 : 0xFF817A7A;
		if (!decoration.index.isEmpty()) graphics.drawString(font, decoration.index, x + 8, textY, primary ? 0xFFFFB2A7 : 0xFFC36B66, false);
		String label = font.plainSubstrByWidth(widget.getMessage().getString(), Math.max(1, w - inset - 16));
		graphics.drawString(font, label, x + inset, textY, color, false);
		if (highlighted && w >= 150) graphics.drawString(font, ">", x + w - 12, textY, color, false);
		return true;
	}

	public static int realmsOffsetX(int screenWidth) { return realmsButton == null ? 0 : realmsButton.getX() + realmsButton.getWidth() - (screenWidth / 2 + 100); }
	public static int realmsOffsetY(int screenHeight) { return realmsButton == null ? 0 : realmsButton.getY() - (screenHeight / 4 + 96); }
}
