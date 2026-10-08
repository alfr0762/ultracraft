package dev.ultracraft;

import java.util.ArrayList;
import java.util.List;
import net.minecraft.client.KeyMapping;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.GuiGraphics;
import net.minecraft.client.gui.components.Button;
import net.minecraft.client.gui.screens.Screen;
import net.minecraft.client.input.KeyEvent;
import net.minecraft.network.chat.CommonComponents;
import net.minecraft.network.chat.Component;
import net.minecraft.util.FormattedCharSequence;
import org.lwjgl.glfw.GLFW;

/** A small, keyboard-accessible guide; it never changes the player's world or settings. */
public final class UcFieldManualScreen extends Screen {
	private static final int PAGE_COUNT = 3;
	private static final int TEXT_COLOR = 0xffd5dbe2;
	private static final int ACCENT_COLOR = 0xffffb551;
	private final Screen parent;
	private final List<ManualLine> lines = new ArrayList<>();
	private int page;
	private int panelX, panelWidth, contentTop, contentBottom, buttonsTop;
	private int contentHeight, scroll;

	private record Section(Component heading, Component body) {}
	private record ManualLine(FormattedCharSequence text, int color) {}

	public UcFieldManualScreen(Screen parent) {
		super(text("title", "Field Manual"));
		this.parent = parent;
	}

	@Override
	protected void init() {
		panelWidth = Math.min(600, Math.max(120, width - 24));
		panelX = (width - panelWidth) / 2;
		int gap = 6;
		// Two rows at narrow GUI scales keep translated labels readable without shortening their text.
		int columns = panelWidth >= 440 ? 4 : 2;
		int rows = 4 / columns;
		int buttonWidth = (panelWidth - gap * (columns - 1)) / columns;
		buttonsTop = height - rows * 20 - (rows - 1) * gap - 14;
		contentTop = 64;
		contentBottom = Math.max(contentTop + font.lineHeight, buttonsTop - 26);
		lines.clear();
		for (Section section : sections()) {
			for (FormattedCharSequence line : font.split(section.heading, panelWidth - 32))
				lines.add(new ManualLine(line, ACCENT_COLOR));
			for (FormattedCharSequence line : font.split(section.body, panelWidth - 32))
				lines.add(new ManualLine(line, TEXT_COLOR));
			lines.add(new ManualLine(FormattedCharSequence.EMPTY, TEXT_COLOR));
		}
		contentHeight = lines.size() * (font.lineHeight + 2);
		scroll = Math.min(scroll, maxScroll());

		Button previous = addRenderableWidget(Button.builder(text("previous", "Previous"), button -> turnPage(-1))
			.bounds(panelX, buttonsTop, buttonWidth, 20).build());
		previous.active = page > 0;
		UcTitleMenu.decorate(previous, "", false);
		Button next = addRenderableWidget(Button.builder(text("next", "Next"), button -> turnPage(1))
			.bounds(panelX + buttonWidth + gap, buttonsTop, buttonWidth, 20).build());
		next.active = page < PAGE_COUNT - 1;
		UcTitleMenu.decorate(next, "", false);
		Button controls = addRenderableWidget(Button.builder(text("controls", "Controls"),
			button -> minecraft.setScreen(new UcKeybindsScreen(this)))
			.bounds(panelX + (2 % columns) * (buttonWidth + gap), buttonsTop + (2 / columns) * (20 + gap), buttonWidth, 20).build());
		UcTitleMenu.decorate(controls, "", false);
		Button back = addRenderableWidget(Button.builder(CommonComponents.GUI_BACK, button -> onClose())
			.bounds(panelX + (3 % columns) * (buttonWidth + gap), buttonsTop + (3 / columns) * (20 + gap), buttonWidth, 20).build());
		UcTitleMenu.decorate(back, "", true);
	}

	private void turnPage(int direction) {
		int next = Math.clamp(page + direction, 0, PAGE_COUNT - 1);
		if (next == page) return;
		page = next;
		scroll = 0;
		rebuildWidgets();
	}

	@Override
	public void renderBackground(GuiGraphics graphics, int mouseX, int mouseY, float delta) {
		// Screen.renderWithTooltipAndSubtitles calls this before render, in its own render stratum.
		UcTitleMenu.paintBackground(graphics, width, height);
	}

	@Override
	public void render(GuiGraphics graphics, int mouseX, int mouseY, float delta) {
		graphics.fill(panelX, 14, panelX + panelWidth, buttonsTop - 12, 0xe6101219);
		graphics.renderOutline(panelX, 14, panelWidth, Math.max(1, buttonsTop - 26), 0xff4b3940);
		graphics.drawCenteredString(font, text("page", "Field Manual  /  %s of %s", page + 1, PAGE_COUNT),
			width / 2, 24, 0xff9ea8b5);
		graphics.drawCenteredString(font, pageTitle(), width / 2, 42, ACCENT_COLOR);
		graphics.enableScissor(panelX + 12, contentTop, panelX + panelWidth - 12, contentBottom);
		int y = contentTop - scroll;
		for (ManualLine line : lines) {
			if (y + font.lineHeight >= contentTop && y < contentBottom)
				graphics.drawString(font, line.text, panelX + 16, y, line.color, false);
			y += font.lineHeight + 2;
		}
		graphics.disableScissor();
		if (maxScroll() > 0) {
			int trackHeight = contentBottom - contentTop;
			int thumbHeight = Math.max(12, trackHeight * trackHeight / contentHeight);
			int thumbY = contentTop + scroll * (trackHeight - thumbHeight) / maxScroll();
			graphics.fill(panelX + panelWidth - 7, contentTop, panelX + panelWidth - 5, contentBottom, 0xff343641);
			graphics.fill(panelX + panelWidth - 7, thumbY, panelX + panelWidth - 5, thumbY + thumbHeight, ACCENT_COLOR);
			graphics.drawCenteredString(font, text("scroll", "Scroll / Page Up / Page Down"),
				width / 2, buttonsTop - 21, 0xff9ea8b5);
		}
		super.render(graphics, mouseX, mouseY, delta);
	}

	@Override
	public boolean mouseScrolled(double mouseX, double mouseY, double horizontal, double vertical) {
		if (maxScroll() > 0 && mouseX >= panelX && mouseX <= panelX + panelWidth
			&& mouseY >= contentTop && mouseY <= contentBottom && vertical != 0) {
			scroll = Math.clamp(scroll - (int) (vertical * (font.lineHeight + 2) * 3), 0, maxScroll());
			return true;
		}
		return super.mouseScrolled(mouseX, mouseY, horizontal, vertical);
	}

	@Override
	public boolean keyPressed(KeyEvent event) {
		if (maxScroll() > 0 && (event.key() == GLFW.GLFW_KEY_PAGE_UP || event.key() == GLFW.GLFW_KEY_PAGE_DOWN)) {
			int direction = event.key() == GLFW.GLFW_KEY_PAGE_UP ? -1 : 1;
			scroll = Math.clamp(scroll + direction * (contentBottom - contentTop - font.lineHeight), 0, maxScroll());
			return true;
		}
		return super.keyPressed(event);
	}

	@Override
	public void onClose() {
		minecraft.setScreen(parent);
	}

	@Override
	public Component getNarrationMessage() {
		var narration = title.copy().append(". ").append(pageTitle());
		for (Section section : sections())
			narration.append(". ").append(section.heading).append(". ").append(section.body);
		return narration;
	}

	private int maxScroll() {
		return Math.max(0, contentHeight - (contentBottom - contentTop));
	}

	private Component pageTitle() {
		return switch (page) {
			case 0 -> text("movement", "Stay in Motion");
			case 1 -> text("weapons", "Make Weapons Work Together");
			default -> text("records", "Know Your Best Run");
		};
	}

	private List<Section> sections() {
		return switch (page) {
			case 0 -> List.of(
				new Section(text("movement.dash.title", "Dash, Slide, Wall-Jump"),
					text("movement.dash.body", "Dash through danger, slide to keep momentum, and wall-jump to change height. Chain them; save stamina for an escape.")),
				new Section(text("movement.keys.title", "Your Minecraft Keys"),
					text("movement.keys.body", "V1 / Steve: %s. Minecraft hands / V1 guns: %s. Camera perspective: %s.",
						minecraftKey("key.ultracraft.toggle"), minecraftKey("key.ultracraft.hands"),
						Minecraft.getInstance().options.keyTogglePerspective.getTranslatedKeyMessage())),
				new Section(text("movement.controls.title", "Make It Yours"),
					text("movement.controls.body", "Open Controls below to check or rebind ULTRAKILL movement, weapons and arms.")));
			case 1 -> List.of(
				new Section(text("weapons.coin.title", "Coin + Railcannon"),
					text("weapons.coin.body", "Toss a Marksman coin, then shoot it with the Electric Railcannon to redirect the shot. Leave space and keep the coin in sight.")),
				new Section(text("weapons.parry.title", "Return to Sender"),
					text("weapons.parry.body", "Punch a parryable incoming projectile with the Feedbacker arm. Time the punch just before impact and aim the returned shot at an enemy.")),
				new Section(text("weapons.style.title", "Keep the Combo Moving"),
					text("weapons.style.body", "Switch weapons between attacks and combine their effects. Practice timing in a safe area before taking the combo into a boss fight.")));
			default -> List.of(
				new Section(text("records.command.title", "Combat Records"),
					text("records.command.body", "In a world, run /uc records to inspect your boss clears and fastest successful fights.")),
				new Section(text("records.fair.title", "Compare Like with Like"),
					text("records.fair.body", "Fastest clears are kept separately for each boss, difficulty and trait combination. A new personal best improves only the matching category.")),
				new Section(text("records.practice.title", "One More Attempt"),
					text("records.practice.body", "Learn a boss's attack cues, keep moving and try a different weapon combo. Check your records after the fight to see your progress.")));
		};
	}

	private static Component minecraftKey(String name) {
		for (KeyMapping mapping : Minecraft.getInstance().options.keyMappings)
			if (mapping.getName().equals(name)) return mapping.getTranslatedKeyMessage();
		return text("unbound", "Unbound");
	}

	private static Component text(String suffix, String fallback, Object... args) {
		return Component.translatableWithFallback("ultracraft.manual." + suffix, fallback, args);
	}
}
