using System;

namespace Palisades.Models
{
    public class NoteItem
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = "Note";
        public string Content { get; set; } = "";
        public double X { get; set; } = 100;
        public double Y { get; set; } = 100;
        public double Width { get; set; } = 220;
        public double Height { get; set; } = 200;
        public string Color { get; set; } = "#FFFDE272";
        public double FontSize { get; set; } = 12;
    }
}
