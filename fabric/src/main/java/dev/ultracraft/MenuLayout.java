package dev.ultracraft;

/** GUI-scaled terminal layout. Pure arithmetic so small and wide windows can be checked without a game. */
public record MenuLayout(int x, int titleY, int width, int firstY, int rowHeight, int gap) {
	public static MenuLayout forSize(int width, int height) {
		int margin = Math.max(12, Math.min(64, width / 14));
		int panel = Math.min(244, Math.max(170, width / 3));
		panel = Math.min(panel, width - margin * 2);
		int row = height < 280 ? 20 : 24;
		int gap = height < 280 ? 3 : 4;
		int total = row * 6 + 6 + gap * 5;
		int top = Math.max(12, (height - total - 72) / 2);
		return new MenuLayout(margin, top, panel, top + 52, row, gap);
	}

	public int rowY(int index) {
		return index == 0 ? firstY : firstY + rowHeight + 6 + gap + (index - 1) * (rowHeight + gap);
	}

	public int primaryHeight() { return rowHeight + 6; }
}
