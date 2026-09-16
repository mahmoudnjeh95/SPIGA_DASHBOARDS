namespace SpigaDashboards.App.Models;

public class NoteEntry
{
    public int NoteId { get; set; }
    public string Content { get; set; } = "";
    public int CreatedByEmployeeId { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }
}
