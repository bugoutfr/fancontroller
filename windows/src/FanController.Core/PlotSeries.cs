namespace FanController.Core;

/// <summary>Point d'une courbe ; une ordonnée absente interrompt le tracé.</summary>
/// <param name="X">Abscisse, en secondes Unix pour un axe temporel.</param>
/// <param name="Y">Ordonnée, ou absence de mesure.</param>
/// <param name="Segment">Identifiant séparant les portions qui ne doivent pas être reliées.</param>
public sealed record PlotPoint(double X, double? Y, string? Segment = null);

/// <summary>Série nommée à afficher dans un graphique.</summary>
/// <param name="Name">Libellé de la légende.</param>
/// <param name="Color">Couleur au format hexadécimal #RRGGBB.</param>
/// <param name="Points">Points dans l'ordre de tracé.</param>
public sealed record PlotSeries(string Name, string Color, IReadOnlyList<PlotPoint> Points);
