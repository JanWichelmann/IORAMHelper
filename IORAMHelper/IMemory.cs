namespace IORAMHelper
{
	/// <summary>
	/// Stellt Funktionen zur Berechnung des Speicherbedarfs einer dieses Interface implementierenden Klasse bereit.
	/// </summary>
	public interface IMemory
	{
		/// <summary>
		/// Schätzt den Speicherbedarf der Klasse und gibt diesen dann zurück.
		/// </summary>
		/// <returns></returns>
		int calcMemoryUsage();
	}
}