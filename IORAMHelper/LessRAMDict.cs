using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;

namespace IORAMHelper
{
	/// <summary>
	/// Repräsentiert eine Dictionary-Implementierung, die den Temp-Ordner zur Zwischenspeicherung sehr großer Arrays mit geringen Zugriffsfrequenzen nutzt. TValue sollte über das SerializableAttribute-Attribut verfügen, sonst wird ein Fehler ausgelöst. Dateigrößen &gt; 2 GB sollten nicht verwendet werden; einmal aus Verarbeitungsgründen und der .NET-bedingten Konvertierung von [signed] 64-Bit-Werten in [signed] 32-Bit-Werte. Die Schlüssel sind im String-Fall case-insensitive.
	/// </summary>
	public class LessRAMDict<TKey, TValue> : IDictionary<TKey, TValue>, IDisposable
	{
		/// <summary>
		/// Der Standard-Start des Dateinamens (Endung: Immer *.ram).
		/// </summary>
		private const string FILENAME_START = "lrdict_";

		/// <summary>
		/// Die Standard-Stream-Puffergröße in Byte.
		/// </summary>
		private const int DEFAULT_BUFFER_SIZE = 4096;

		/// <summary>
		/// Die Standard-Cache-Größe in Byte.
		/// </summary>
		private const int DEFAULT_CACHE_SIZE = 104857600; // 100 MB

		/// <summary>
		/// Speichert den von dem Dictionary verwendeten Dateinamen.
		/// </summary>
		private string _dictFileName = "";

		/// <summary>
		/// Der Stream zur temporären Datei.
		/// </summary>
		private FileStream _fs = null;

		/// <summary>
		/// Enthält die Pointer auf die einzelnen Dateiinhalte. value[0]: Der Pointer. value[1]: Die Länge des Inhalts.
		/// </summary>
		private Dictionary<TKey, PointerPair[]> _pointers = null;

		/// <summary>
		/// Enthält eine Auflistung aller unbenutzten Stellen in der Datei. Diese Auflistung ist _pointers gegenüber strenggenommen zwar redundant, bietet dadurch aber enorme Performancevorteile.
		/// </summary>
		private List<PointerPair> _unusedPointers = null;

		/// <summary>
		/// Die aktuelle Cache-Größe.
		/// </summary>
		private int _currentCacheSize = 0;

		/// <summary>
		/// Speichert Daten zwischen, um nicht zu oft kleinste Pakete auf die Festplatte schreiben zu müssen.
		/// </summary>
		private Dictionary<TKey, TValue> _cache = null;

		/// <summary>
		/// Konstruktor. Erstellt ein neues LessRAMDictionary.
		/// </summary>
		/// <param name="dictID">Die ID des Dictionaries. Diese ID sollte eindeutig sein, sonst könnte es zu Konflikten mit anderen LessRAMDict-Objekten kommen.</param>
		public LessRAMDict(string dictID)
		{
			// Prüfen, ob für den übergebenen Wert-Typen das "SerializableAttribute"-Attribut gesetzt ist
			if(typeof(TValue).GetCustomAttributes(typeof(SerializableAttribute), true).Length == 0)
			{
				// Dann gehts hier nicht weiter...die Klasse darf nicht initialisiert werden.
				throw new Exception("Für den übergebenen Wert-Typen ist das \"SerializableAttribute\"-Attribut ist nicht gesetzt!");
			}

			// ID von ungültigen Zeichen bereinigen
			foreach(char z in Path.GetInvalidFileNameChars())
			{
				// Zeichen ersetzen
				dictID = dictID.Replace(z, '_');
			}

			// Dateinamen generieren
			_dictFileName = Path.GetTempPath() + FILENAME_START + dictID + ".ram";

			// Fehler abfangen
			try
			{
				// Temporäre Datei ggf. löschen
				if(File.Exists(_dictFileName))
					File.Delete(_dictFileName);

				// Temporäre Datei erstellen und dabei Dateistream öffnen
				_fs = new FileStream(_dictFileName, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
			}
			catch(Exception ex)
			{
				// Fehler
				throw new Exception("Fehler beim Erstellen der temporären Datei: " + ex.Message);
			}

			// Pointer-Arrays initialisieren
			_pointers = new Dictionary<TKey, PointerPair[]>();
			_unusedPointers = new List<PointerPair>();

			// Cache initialisieren
			_cache = new Dictionary<TKey, TValue>();
		}

		/// <summary>
		/// Destruktor. Löscht die temporäre Datei von dem Computer.
		/// </summary>
		~LessRAMDict()
		{
			// Sicherstellen, dass diese Vorgänge noch nicht aufgerufen wurden (Fehler ignorieren)
			try
			{
				// Stream beenden
				_fs.Close();

				// Datei ggf. löschen
				if(File.Exists(_dictFileName))
					File.Delete(_dictFileName);
			}
			catch { }
		}

		/// <summary>
		/// Fügt den angegebenen Wert mit dem angegebenen Schlüssel hinzu.
		/// </summary>
		/// <param name="key">Der Schlüssel.</param>
		/// <param name="value">Der Wert.</param>
		public void Add(TKey key, TValue value)
		{
			// Falls es sich um einen String-Schlüssel handelt, diesen case-insensitive setzen
			if(typeof(TKey) == typeof(string))
			{
				key = (TKey)((object)key.ToString().ToLower());
			}

			// Sollte der aktuelle Datentyp erstmal in den Cache geschrieben werden?
			_cache.Add(key, value); // Wird sonst später direkt wieder herausgenommen
			if(typeof(TValue) == typeof(IMemory))
			{
				// Cache-Größe prüfen
				int mem = ((IMemory)value).calcMemoryUsage();
				if(mem + _currentCacheSize < DEFAULT_CACHE_SIZE)
				{
					// Ggf. existierende Werte zu diesem Schlüssel löschen
					Remove(key);

					// In den Cache schreiben und Funktion beenden
					_currentCacheSize += mem;
					return;
				}
				else
				{
					// Passt nicht in den Cache, also Cache leeren
				}
			}

			// Alle Werte des Caches schreiben
			foreach(KeyValuePair<TKey, TValue> aktC in _cache)
			{
				// BinaryFormatter für das Umwandeln der Daten verwenden
				MemoryStream temp = new MemoryStream();
				BinaryFormatter bf = new BinaryFormatter();
				{
					// Fehler abfangen
					try
					{
						// Serialisieren und in Hilfs-Stream schreiben
						bf.Serialize(temp, aktC.Value);
					}
					catch(Exception ex)
					{
						// Fehler auswerfen
						throw new Exception("Das Objekt konnte nicht serialisiert werden: " + ex.Message);
					}
				}

				// Puffer anlegen
				byte[] buffer;

				// Länge speichern
				long length = temp.Length;

				// Zum Stream-Anfang zurück
				temp.Position = 0;

				// Pointerpaar initialisieren
				List<PointerPair> pps = new List<PointerPair>();

				// Länge zerlegen
				long remainingBytes = length;
				int realCount = 0;
				List<int> usedPointerIndexes = new List<int>();
				for(int i = 0; i < _unusedPointers.Count; i++)
				{
					// Schon fertig?
					if(remainingBytes <= 0)
						break;

					// Aktuellen unbenutzten Pointer abrufen
					PointerPair aktP = _unusedPointers[i];

					// Pointerlänge von den verbleibenden Bytes abziehen und Pointer in die Liste schreiben
					remainingBytes -= aktP._length;
					usedPointerIndexes.Add(i);

					// Puffer je nach Größe initialisieren
					buffer = new byte[aktP._length];

					// Wert in Puffer laden
					realCount = temp.Read(buffer, 0, (int)aktP._length);

					// Wert in Stream schreiben
					_fs.Position = aktP._pointer;
					_fs.Write(buffer, 0, realCount);
					_fs.Flush();

					// Pointer-Paar merken
					pps.Add(aktP);
				}

				// Wenn die unbenutzten Pointer nicht ausgereicht haben, den Rest ans Ende schreiben
				if(remainingBytes > 0)
				{
					// Verbliebene Daten in Puffer laden
					buffer = new byte[remainingBytes];
					temp.Read(buffer, 0, (int)remainingBytes);

					// Neuen Pointer anlegen
					PointerPair newP = new PointerPair(_fs.Length, remainingBytes);

					// Daten in Puffer schreiben
					_fs.Position = _fs.Length;
					_fs.Write(buffer, 0, (int)remainingBytes);
					_fs.Flush();

					// Pointer hinzufügen
					pps.Add(newP);
				}

				// Temporären Stream vernichten
				temp.Close();
				temp.Dispose();

				// Verwendete Pointerlücken löschen
				int delCount = 0;
				for(int i = 0; i < usedPointerIndexes.Count; i++)
				{
					// Der letzte Pointer braucht eine Sonderbehandlung
					if(i == usedPointerIndexes.Count - 1)
					{
						// Jetzt benutzten Pointer abrufen
						PointerPair aktP = _unusedPointers[usedPointerIndexes[i - delCount]];

						// Der Pointer muss nicht gelöscht, sondern nur aktualisiert werden (nur, wenn er vollständig beschrieben wurde)
						if(aktP._length == realCount)
						{
							// Pointer löschen
							_unusedPointers.RemoveAt(usedPointerIndexes[i] - delCount);
						}
						else
						{
							// Pointer aktualisieren
							aktP._pointer += realCount;
							aktP._length -= realCount;
							_unusedPointers[usedPointerIndexes[i] - delCount] = aktP;

							// Letztes Pointerpaar in Pointerliste aktualisieren
							aktP = pps[pps.Count - 1];
							aktP._length = realCount;
							pps[pps.Count - 1] = aktP;
						}
					}
					else
					{
						// Pointer löschen
						_unusedPointers.RemoveAt(usedPointerIndexes[i] - delCount);

						// Es wurde ein Element gelöscht, d.h. alle Listenelemente rücken einen Index nach unten Die Liste wurde von oben gelesen, d.h. dieses Vorgehen ist in Ordnung
						delCount++;
					}
				}

				// Erstellte Pointerliste in Schlüssel-Pointer-Liste schreiben
				_pointers.Add(aktC.Key, pps.ToArray());
			}

			// Cache ist leer
			_cache.Clear();
			_currentCacheSize = 0;
		}

		/// <summary>
		/// Gibt den dem angegebenen Schlüssel zugeordneten Wert zurück.
		/// </summary>
		/// <param name="key"></param>
		private TValue GetValueByKey(TKey key)
		{
			// Falls es sich um einen String-Schlüssel handelt, diesen case-insensitive setzen
			if(typeof(TKey) == typeof(string))
			{
				key = (TKey)((object)key.ToString().ToLower());
			}

			// Ist der Wert im Cache? => Cache geht vor
			if(_cache.ContainsKey(key))
			{
				// Wert aus dem Cache zurückgeben
				return _cache[key];
			}

			// Pointer abrufen
			PointerPair[] pps = _pointers[key];

			// Temporären Stream erstellen
			MemoryStream temp = new MemoryStream();

			// Puffer anlegen
			byte[] buffer;

			// Fragmente in temporären Stream lesen
			foreach(PointerPair aktP in pps)
			{
				// In Puffer lesen
				buffer = new byte[aktP._length];
				_fs.Position = aktP._pointer;
				_fs.Read(buffer, 0, (int)aktP._length);

				// In temporären Stream schreiben
				temp.Write(buffer, 0, (int)aktP._length);
			}

			// Zurück an den Anfang
			temp.Position = 0;

			// BinaryFormatter für das Umwandeln der Daten verwenden
			BinaryFormatter bf = new BinaryFormatter();
			{
				// Fehler abfangen
				try
				{
					// Deserialisieren
					return (TValue)(bf.Deserialize(temp));
				}
				catch(Exception ex)
				{
					// Fehler auswerfen
					throw new Exception("Das Objekt konnte nicht deserialisiert werden: " + ex.Message);
				}
			}
		}

		/// <summary>
		/// Überprüft, ob der angegebene Schlüssel bereits in der Auflistung enthalten ist.
		/// </summary>
		/// <param name="key">Der Schlüssel.</param>
		/// <returns></returns>
		public bool ContainsKey(TKey key)
		{
			// Falls es sich um einen String-Schlüssel handelt, diesen case-insensitive setzen
			if(typeof(TKey) == typeof(string))
			{
				key = (TKey)((object)key.ToString().ToLower());
			}

			// Schlüssel suchen
			return _cache.ContainsKey(key) || _pointers.ContainsKey(key);
		}

		/// <summary>
		/// Gibt eine Liste aller definierten Schlüssel zurück.
		/// </summary>
		public ICollection<TKey> Keys
		{
			get
			{
				// Schlüsselliste zurückgeben
				return new List<TKey>(_pointers.Keys.Concat(_cache.Keys));
			}
		}

		/// <summary>
		/// Löscht den angegebenen Schlüssel samt Wert aus der Auflistung.
		/// </summary>
		/// <param name="key">Der Schlüssel des zu löschenden Elements.</param>
		/// <returns></returns>
		public bool Remove(TKey key)
		{
			try
			{
				// Falls es sich um einen String-Schlüssel handelt, diesen case-insensitive setzen
				if(typeof(TKey) == typeof(string))
				{
					key = (TKey)((object)key.ToString().ToLower());
				}

				// Wert aus Cache löschen
				if(_cache.ContainsKey(key))
					_cache.Remove(key);

				// Ggf. Wert aus Datei löschen
				if(_pointers.ContainsKey(key))
				{
					// Alle Pointer des Elements in die Liste der unbenutzten Pointer verschieben
					foreach(PointerPair aktP in _pointers[key])
					{
						// Zur "Unbenutzte Pointer"-Liste hinzufügen
						_unusedPointers.Add(aktP);
					}

					// Pointer-Listenelement löschen
					_pointers.Remove(key);
				}

				// Alles gut
				return true;
			}
			catch(Exception ex)
			{
				// Mist
				throw new Exception("Schwerwiegender Fehler: " + ex.Message);
			}
		}

		/// <summary>
		/// Versucht, einen Wert zum angegebenen Schlüssel auszulesen. Bei Erfolg wird der Wert in den Parameter geschrieben und true zurückgegeben, andernfalls der Standardwert und false.
		/// </summary>
		/// <param name="key">Der Schlüssel, nach dem gesucht werden soll.</param>
		/// <param name="value">Die Variable, in die der evtl. gefundene Wert geschrieben werden soll.</param>
		/// <returns></returns>
		public bool TryGetValue(TKey key, out TValue value)
		{
			// Fehler ignorieren
			try
			{
				// Falls es sich um einen String-Schlüssel handelt, diesen case-insensitive setzen
				if(typeof(TKey) == typeof(string))
				{
					key = (TKey)((object)key.ToString().ToLower());
				}

				// Wert zurückgeben
				value = GetValueByKey(key);
				return true;
			}
			catch
			{
				// Fehler, Standardwert zurückgeben
				value = default(TValue);
				return false;
			}
		}

		/// <summary>
		/// Gibt eine Liste aller definierten Werte zurück.
		/// Vorsicht: Gegebenenfalls sehr hohe Arbeitsspeicherbelastung!
		/// </summary>
		public ICollection<TValue> Values
		{
			get
			{
				// Wertliste erstellen
				List<TValue> valueList = new List<TValue>();

				// Alle Werte abrufen
				foreach(TKey aktK in _pointers.Keys)
				{
					valueList.Add(GetValueByKey(aktK));
				}
				foreach(TKey aktK in _cache.Keys)
				{
					valueList.Add(GetValueByKey(aktK));
				}

				// Fertig
				return valueList;
			}
		}

		/// <summary>
		/// Gibt den zum Schlüssel gehörenden Wert zurück.
		/// </summary>
		/// <param name="key">Der Schlüssel, dessen zugehöriger Wert gesucht wird.</param>
		/// <returns></returns>
		public TValue this[TKey key]
		{
			get
			{
				// Falls es sich um einen String-Schlüssel handelt, diesen case-insensitive setzen
				if(typeof(TKey) == typeof(string))
				{
					key = (TKey)((object)key.ToString().ToLower());
				}

				// Wert abrufen
				return GetValueByKey(key);
			}
			set
			{
				// Falls es sich um einen String-Schlüssel handelt, diesen case-insensitive setzen
				if(typeof(TKey) == typeof(string))
				{
					key = (TKey)((object)key.ToString().ToLower());
				}

				// Einfach den Schlüssel mitsamt Pointern löschen (effizient, da die Auslagerungsdatei selbst dabei nicht verändert wird)
				Remove(key);

				// Gelöschten Wert überschreiben
				Add(key, value);
			}
		}

		/// <summary>
		/// Fügt ein neues Element der Auflistung hinzu.
		/// </summary>
		/// <param name="item">Das hinzuzufügende Element.</param>
		public void Add(KeyValuePair<TKey, TValue> item)
		{
			// Falls es sich um einen String-Schlüssel handelt, diesen case-insensitive setzen
			if(typeof(TKey) == typeof(string))
			{
				item = new KeyValuePair<TKey, TValue>((TKey)((object)item.Key.ToString().ToLower()), item.Value);
			}

			// Andere Add()-Funktion aufrufen
			Add(item.Key, item.Value);
		}

		/// <summary>
		/// Löscht alle Daten aus der Auflistung.
		/// </summary>
		public void Clear()
		{
			// Cache löschen
			_cache.Clear();
			_currentCacheSize = 0;

			// Datei einfach neu erstellen
			{
				// Stream beenden
				_fs.Close();

				// Datei löschen
				File.Delete(_dictFileName);

				// Fehler abfangen
				try
				{
					// Temporäre Datei erstellen und dabei Dateistream öffnen
					_fs = new FileStream(_dictFileName, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
				}
				catch(Exception ex)
				{
					// Fehler
					throw new Exception("Fehler beim Erstellen der temporären Datei: " + ex.Message);
				}
			}

			// Pointer-Array neu initialisieren
			_pointers = new Dictionary<TKey, PointerPair[]>();
		}

		/// <summary>
		/// Prüft, ob ein Element bereits in der Auflistung vorliegt.
		/// </summary>
		/// <param name="item">Das zu suchende Element.</param>
		/// <returns></returns>
		public bool Contains(KeyValuePair<TKey, TValue> item)
		{
			// Falls es sich um einen String-Schlüssel handelt, diesen case-insensitive setzen
			if(typeof(TKey) == typeof(string))
			{
				item = new KeyValuePair<TKey, TValue>((TKey)((object)item.Key.ToString().ToLower()), item.Value);
			}

			// Nach Schlüssel suchen
			if(!_pointers.ContainsKey(item.Key) && !_cache.ContainsKey(item.Key))
				return false;

			// Schlüssel gefunden, Wert abrufen und vergleichen
			if(!GetValueByKey(item.Key).Equals(item.Value))
				return false;

			// Alles OK, Objekt enthalten
			return true;
		}

		/// <summary>
		/// Kopiert alle Werte in ein Array.
		/// </summary>
		/// <param name="array">Das Ausgabearray.</param>
		/// <param name="arrayIndex">Der Index im Ausgabearray, ab dem Werte eingefügt werden sollen.</param>
		public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
		{
			// Arraygröße ermitteln
			int newSize = 0;
			if(arrayIndex == 0)
				newSize = _pointers.Count;
			else
				newSize = arrayIndex + _pointers.Count;

			// Falls das Zielarray zu groß ist, dessen Größe nehmen
			if(array.Length > newSize)
				newSize = array.Length;

			// Temporäres Array mit ausreichender Größe erstellen
			KeyValuePair<TKey, TValue>[] temp = new KeyValuePair<TKey, TValue>[newSize];

			// Ausgabearray in Temporäres Array kopieren
			if(array != null)
				Array.Copy(array, temp, Math.Min(array.Length, temp.Length));

			// Temporäres Array über Ausgabearray schreiben
			array = temp;

			// Temporäres Array vernichten (Speicher sparen)
			temp = null;

			// Eigene Werte in Zielarray schreiben
			int i = arrayIndex;
			foreach(TKey key in _pointers.Keys)
			{
				// Wert abrufen
				array[i] = new KeyValuePair<TKey, TValue>(key, GetValueByKey(key));

				// Nächster
				i++;
			}
			foreach(TKey key in _cache.Keys)
			{
				// Wert abrufen
				array[i] = new KeyValuePair<TKey, TValue>(key, GetValueByKey(key));

				// Nächster
				i++;
			}
		}

		/// <summary>
		/// Gibt die Anzahl der enthaltenen Elemente zurück.
		/// </summary>
		public int Count
		{
			get
			{
				// Länge der Schlüsselliste reicht aus
				return _pointers.Count + _cache.Count;
			}
		}

		/// <summary>
		/// Gibt an, ob die Auslistung im Moment schreibgeschützt ist.
		/// </summary>
		public bool IsReadOnly
		{
			get
			{
				// Niemals
				return false;
			}
		}

		/// <summary>
		/// Löscht das angegebene Element aus der Auflistung und gibt im Erfolgsfall false zurück.
		/// </summary>
		/// <param name="item">Das zu löschende Element.</param>
		/// <returns></returns>
		public bool Remove(KeyValuePair<TKey, TValue> item)
		{
			// Falls es sich um einen String-Schlüssel handelt, diesen case-insensitive setzen
			if(typeof(TKey) == typeof(string))
			{
				item = new KeyValuePair<TKey, TValue>((TKey)((object)item.Key.ToString().ToLower()), item.Value);
			}

			// Existiert das Element überhaupt? => Andere Löschfunktion aufrufen
			if(Contains(item))
				return Remove(item.Key);

			// Sonst existiert das Element nicht
			return false;
		}

		/// <summary>
		/// Gibt einen Enumerator zurück, der das Iterieren durch die einzelnen Elemente erlaubt.
		/// </summary>
		/// <returns></returns>
		public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
		{
			// Enumerator zurückgeben
			return new Enumerator(this);
		}

		/// <summary>
		/// Gibt einen Enumerator zurück, der das Iterieren durch die einzelnen Elemente erlaubt.
		/// </summary>
		/// <returns></returns>
		IEnumerator IEnumerable.GetEnumerator()
		{
			// Enumerator zurückgeben
			return ((IDictionary)this).GetEnumerator();
		}

		/// <summary>
		/// Löscht alle internen Variablen und die interne Speicherdatei samt allen Streams.
		/// </summary>
		public void Dispose()
		{
			// Fehler ignorieren, das Objekt soll einfach nur vernichtet werden
			try
			{
				// Stream beenden
				_fs.Close();

				// Datei ggf. löschen
				if(File.Exists(_dictFileName))
					File.Delete(_dictFileName);

				// Pointerlisten löschen
				_pointers = null;
				_unusedPointers = null;

				// Cache löschen
				_cache.Clear();
				_currentCacheSize = 0;
			}
			catch { }
		}

		#region Strukturen

		/// <summary>
		/// Definiert einen Pointer verbunden mit seiner Länge.
		/// </summary>
		private struct PointerPair
		{
			/// <summary>
			/// Der Pointer.
			/// </summary>
			public long _pointer;

			/// <summary>
			/// Die Pointer-Länge.
			/// </summary>
			public long _length;

			/// <summary>
			/// Erstellt ein neues Pointer-Paar.
			/// </summary>
			/// <param name="pointer">Der Pointer.</param>
			/// <param name="length">Die Pointer-Länge.</param>
			public PointerPair(long pointer, long length)
			{
				// Werte zuweisen
				_pointer = pointer;
				_length = length;
			}
		}

		#endregion Strukturen

		#region Enumeratoren

		/// <summary>
		/// Erlaubt das Durchlaufen der einzelnen LessRAMDict-Elemente.
		/// </summary>
		public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
		{
			/// <summary>
			/// Das zu durchlaufende LessRAMDict-Objekt.
			/// </summary>
			private LessRAMDict<TKey, TValue> _lrd;

			/// <summary>
			/// Der aktuelle Index im Objekt.
			/// </summary>
			private int _index;

			/// <summary>
			/// Erstellt einen neuen Enumerator.
			/// </summary>
			/// <param name="lrd"></param>
			public Enumerator(LessRAMDict<TKey, TValue> lrd)
			{
				// Objekt speichern
				_lrd = lrd;

				// Index speichern
				_index = -1;
			}

			/// <summary>
			/// Gibt das aktuelle Element zurück.
			/// </summary>
			public KeyValuePair<TKey, TValue> Current
			{
				get
				{
					// Schlüssel abrufen
					TKey aktK = default(TKey);
					if(_index >= _lrd._pointers.Count)
					{
						aktK = _lrd._cache.Keys.ElementAt(_index - _lrd._pointers.Count);
					}
					else
					{
						aktK = _lrd._pointers.Keys.ElementAt(_index);
					}

					// Wert abrufen
					TValue aktV = _lrd.GetValueByKey(aktK);

					// Fertig
					return new KeyValuePair<TKey, TValue>(aktK, aktV);
				}
			}

			/// <summary>
			/// Gibt das aktuelle Element zurück.
			/// </summary>
			object IEnumerator.Current
			{
				get
				{
					// Schlüssel abrufen
					TKey aktK = default(TKey);
					if(_index >= _lrd._pointers.Count)
					{
						aktK = _lrd._cache.Keys.ElementAt(_index - _lrd._pointers.Count);
					}
					else
					{
						aktK = _lrd._pointers.Keys.ElementAt(_index);
					}

					// Wert abrufen
					TValue aktV = _lrd.GetValueByKey(aktK);

					// Fertig
					return new KeyValuePair<TKey, TValue>(aktK, aktV);
				}
			}

			/// <summary>
			/// Geht zum nächsten Element über und gibt bei dessen Existenz true zurück, sonst false.
			/// </summary>
			/// <returns></returns>
			public bool MoveNext()
			{
				// Index um 1 erhöhen
				_index++;

				// Prüfen, ob der Index überhaupt noch existiert
				if(_index >= _lrd._pointers.Count + _lrd._cache.Count)
					return false; // Fertig
				else
					return true; // Nächster
			}

			/// <summary>
			/// Setzt den Durchlauf-Vorgang zurück.
			/// </summary>
			public void Reset()
			{
				// Index zurücksetzen
				_index = -1;
			}

			/// <summary>
			/// Gibt den vom Enumerator belegten Speicher frei.
			/// </summary>
			public void Dispose()
			{
				// Liste löschen
				_lrd = null;
			}
		}

		#endregion Enumeratoren
	}
}