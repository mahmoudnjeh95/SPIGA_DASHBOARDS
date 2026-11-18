namespace SpigaDashboards.App.Models;

// The dashboard's sticky note (application database, table Notes).
public class NoteEntry
{
    public int NoteId { get; set; }
    public string Content { get; set; } = "";
    // Login of the account that wrote it ("kiosque" from a TV PC).
    public string CreatedBy { get; set; } = "";
    public string CreatedByName { get; set; } = "";
    public string Workstation { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }
}
