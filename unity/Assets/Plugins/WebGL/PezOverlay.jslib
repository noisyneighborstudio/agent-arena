// Bridge from the game to the page's commander box (WebGLTemplates/PezWeb/index.html owns the HTML; this only forwards).
mergeInto(LibraryManager.library, {
  // Unity screen pixels (top left origin) of the rect to cover; the page maps them through the canvas's CSS size.
  PezCommanderBox: function (x, y, w, h, visible, screenW, screenH) {
    if (window.PezCommander) window.PezCommander.place(x, y, w, h, visible !== 0, screenW, screenH);
  },
  PezCommanderStatus: function (text) {
    if (window.PezCommander) window.PezCommander.status(UTF8ToString(text));
  },
  PezCommanderText: function (text, force) {
    if (window.PezCommander) window.PezCommander.setText(UTF8ToString(text), force !== 0);
  },
});
