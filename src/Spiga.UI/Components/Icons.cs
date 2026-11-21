using Microsoft.AspNetCore.Components;

namespace Spiga.UI.Components;

// Line icons (24x24, stroke = currentColor, drawn by spiga.css) for the
// navigation rail and the header buttons. Inline SVG: no icon font to
// download, the plant PCs may be offline.
public static class Icons
{
    private static readonly Dictionary<string, string> Paths = new()
    {
        // navigation, by NavItem.Key
        ["vue-ensemble"] = "<rect x='3' y='3' width='7' height='9' rx='1.5'/><rect x='14' y='3' width='7' height='5' rx='1.5'/><rect x='14' y='12' width='7' height='9' rx='1.5'/><rect x='3' y='16' width='7' height='5' rx='1.5'/>",
        ["conditionnement"] = "<path d='M21 8 12 3 3 8v8l9 5 9-5z'/><path d='m3 8 9 5 9-5'/><path d='M12 13v8'/>",
        ["tableau"] = "<rect x='2' y='3' width='20' height='14' rx='2'/><path d='M8 21h8M12 17v4'/><path d='m7 12 3-3 2 2 4-4'/>",
        ["rendement"] = "<path d='M22 7 13.5 15.5 8.5 10.5 2 17'/><path d='M16 7h6v6'/>",
        ["silos"] = "<ellipse cx='12' cy='5' rx='8' ry='3'/><path d='M4 5v14c0 1.7 3.6 3 8 3s8-1.3 8-3V5'/><path d='M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3'/>",
        ["dechets"] = "<path d='M3 6h18M8 6V4h8v2M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6'/><path d='M10 11v6M14 11v6'/>",
        ["humidite"] = "<path d='M12 2.7 6.3 8.4a8 8 0 1 0 11.4 0z'/>",
        ["maintenance"] = "<path d='M22 12h-4l-3 9L9 3l-3 9H2'/>",
        ["arrets"] = "<path d='M7.9 2h8.2L22 7.9v8.2L16.1 22H7.9L2 16.1V7.9z'/><path d='M10 9v6M14 9v6'/>",
        ["planification"] = "<rect x='3' y='4' width='18' height='18' rx='2'/><path d='M16 2v4M8 2v4M3 10h18'/><path d='M8 14h.01M12 14h.01M16 14h.01M8 18h.01M12 18h.01'/>",
        ["qualification"] = "<path d='M20.6 13.4 13.4 20.6a2 2 0 0 1-2.8 0L3 13V3h10l7.6 7.6a2 2 0 0 1 0 2.8z'/><circle cx='7.5' cy='7.5' r='1.5'/>",
        ["statistiques-arrets"] = "<path d='M3 3v18h18'/><rect x='7' y='12' width='3' height='6' rx='.5'/><rect x='12' y='7' width='3' height='11' rx='.5'/><rect x='17' y='10' width='3' height='8' rx='.5'/>",
        ["couts"] = "<path d='M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.8-3.8a6 6 0 0 1-7.9 7.9l-6.9 6.9a2.1 2.1 0 0 1-3-3l6.9-6.9a6 6 0 0 1 7.9-7.9z'/>",
        ["energie"] = "<path d='M13 2 3 14h9l-1 8 10-12h-9z'/>",
        ["absences"] = "<path d='M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2'/><circle cx='9' cy='7' r='4'/><path d='M22 21v-2a4 4 0 0 0-3-3.9M16 3.1a4 4 0 0 1 0 7.8'/>",
        ["saisie-emballage"] = "<rect x='8' y='2' width='8' height='4' rx='1'/><path d='M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2'/><path d='m9 14 2 2 4-4'/>",
        ["saisie-produit"] = "<path d='M12 3v18M7 21h10M3 7h18'/><path d='m6 7-3 7a3 3 0 0 0 6 0zM18 7l-3 7a3 3 0 0 0 6 0z'/>",
        ["poste"] = "<path d='M2 20h20'/><rect x='4' y='9' width='7' height='11' rx='1'/><path d='M11 13h9v7h-9z'/><path d='M7 9V5h3l1 4'/><circle cx='15.5' cy='16.5' r='1.5'/>",
        ["a-valider"] = "<path d='M9 11l3 3L22 4'/><path d='M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11'/>",
        ["saisie-rendement"] = "<path d='M12 20h9'/><path d='M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z'/>",
        ["saisie-energie"] = "<path d='m12 14 4-4'/><path d='M3.3 19a10 10 0 1 1 17.4 0'/>",
        ["acces"] = "<path d='M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z'/><circle cx='12' cy='10' r='2.5'/><path d='M8.5 16a4 4 0 0 1 7 0'/>",
        ["objectifs"] = "<circle cx='12' cy='12' r='10'/><circle cx='12' cy='12' r='6'/><circle cx='12' cy='12' r='2'/>",
        ["referentiels"] = "<path d='M4 19.5A2.5 2.5 0 0 1 6.5 17H20V2H6.5A2.5 2.5 0 0 0 4 4.5z'/><path d='M4 19.5A2.5 2.5 0 0 0 6.5 22H20v-5'/>",
        ["sources"] = "<path d='M12 22v-5M9 8V2M15 8V2M18 8v5a6 6 0 0 1-12 0V8z'/>",
        ["audit"] = "<path d='M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01'/>",
        ["parametres"] = "<path d='M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6'/>",
        // header
        ["pointage"] = "<rect x='4' y='3' width='16' height='18' rx='2'/><path d='M9 3v3h6V3'/><path d='m8.5 13 2.5 2.5 4.5-5'/>",
        ["personnel"] = "<circle cx='9' cy='7' r='4'/><path d='M3 21v-2a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v2'/><path d='M16 3.1a4 4 0 0 1 0 7.8M21 21v-2a4 4 0 0 0-3-3.9'/>",
        ["polyvalence"] = "<path d='M12 2 15 8.5 22 9.3 17 14 18.2 21 12 17.8 5.8 21 7 14 2 9.3 9 8.5z'/>",
        ["calendrier"] = "<rect x='3' y='4' width='18' height='18' rx='2'/><path d='M16 2v4M8 2v4M3 10h18'/><path d='m9 15 2 2 4-4'/>",
        ["print"] = "<path d='M6 9V2h12v7'/><rect x='2' y='9' width='20' height='9' rx='2'/><path d='M6 14h12v8H6z'/>",
        ["download"] = "<path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4'/><path d='m7 10 5 5 5-5'/><path d='M12 15V3'/>",
        ["sun"] = "<circle cx='12' cy='12' r='4'/><path d='M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4'/>",
        ["moon"] = "<path d='M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9z'/>",
        ["user"] = "<circle cx='12' cy='8' r='4'/><path d='M4 21a8 8 0 0 1 16 0'/>",
        ["logout"] = "<path d='M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4'/><path d='m16 17 5-5-5-5M21 12H9'/>",
        ["more"] = "<path d='m6 9 6 6 6-6'/>",
        ["less"] = "<path d='m18 15-6-6-6 6'/>",
        ["tv"] = "<rect x='2' y='7' width='20' height='15' rx='2'/><path d='m17 2-5 5-5-5'/>",
        ["clock"] = "<circle cx='12' cy='12' r='9'/><path d='M12 7v5l3 2'/>",
    };

    private const string Fallback = "<circle cx='12' cy='12' r='3'/>";

    public static MarkupString Svg(string key)
        => new($"<svg viewBox='0 0 24 24' aria-hidden='true'>{Paths.GetValueOrDefault(key, Fallback)}</svg>");
}
