using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace IORAMHelper
{
	/// <summary>
	/// Definiert einen Puffer, der ganze Dateien einlesen und byteweise durchgehen kann. Diese Klasse ist nicht threadsicher.
	/// </summary>
	[Serializable()]
	public class RAMBuffer : ICloneable
	{
		/// <summary>
		/// Die enthaltenen Bytedaten.
		/// </summary>
		private readonly List<byte> _data;

		/// <summary>
		/// Die aktuelle Position im Puffer.
		/// </summary>
		private int _pos = 0;

		/// <summary>
		/// Hilfsarray für die internen Konvertierungsoperationen. Es hat immer die Länge 8. In dieses kann beliebig geschrieben werden.
		/// </summary>
		private readonly byte[] _internalHelpBuffer = new byte[8];

		/// <summary>
		/// Erstellt ein neues RAMBuffer-Objekt.
		/// </summary>
		public RAMBuffer()
		{
			// Datenarray initialisieren
			_data = new List<byte>();
		}

		/// <summary>
		/// Erstellt ein neues RAMBuffer-Objekt aus den übergebenen Daten.
		/// </summary>
		/// <param name="data">Die einzufügenden Daten.</param>
		public RAMBuffer(byte[] data)
		{
			// Datenarray mit übergebenen Daten anlegen
			_data = new List<byte>(data);
		}

		/// <summary>
		/// Erstellt ein neues RAMBuffer-Objekt aus dem übergebenen Stream.
		/// </summary>
		/// <param name="stream">Der auszulesende Stream.</param>
		public RAMBuffer(Stream stream)
		{
			// Stream lesen
			using(MemoryStream ms = new MemoryStream())
			{
				// Daten auslesen
				stream.CopyTo(ms);

				// Datenarray mit gelesenen Daten anlegen
				_data = new List<byte>(ms.ToArray());
			}
		}

		/// <summary>
		/// Erstellt ein neues RAMBuffer-Objekt aus der angegebenen Datei.
		/// </summary>
		/// <param name="filename">Die zu ladende Datei.</param>
		public RAMBuffer(string filename)
		{
			// Puffer erstellen
			byte[] buffer;

			// Datei laden
			using(BinaryReader r = new BinaryReader(File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read)))
			{
				// Puffer initialisieren
				buffer = new byte[r.BaseStream.Length];

				// Puffer mit sämtlichen Dateidaten füllen
				r.Read(buffer, 0, (int)r.BaseStream.Length);

				// Stream schließen
				r.Close();
			}

			// Datenarray mit Pufferinhalt initialisieren
			_data = new List<byte>(buffer);
		}

		/// <summary>
		/// Liest ab der aktuellen Position die angegebene Menge von Bytes in ein angegebenes Byte-Array und gibt die Zahl der gelesenen Bytes zurück. Wenn nicht so viele Bytes wie gefordert gelesen werden können, wird eine Exception ausgelöst.
		/// </summary>
		/// <param name="buffer">Der Puffer, in den die Daten gelesen werden sollen (muss nicht initialisiert sein).</param>
		/// <param name="length">Die Länge der zu lesenden Daten.</param>
		/// <returns></returns>
		public int Read(out byte[] buffer, int length)
		{
			// Feststellen, ob noch genug lesbare Daten vorhanden sind
			if(_pos == _data.Count || _pos + length > _data.Count)
			{
				// Es können keine Daten gelesen werden, Exception auslösen
				throw new IndexOutOfRangeException("Error reading " + length + " bytes from buffer: Not enough bytes to read until buffer ends.");
			}

			// Puffer sicherheitshalber initialisieren
			buffer = new byte[length];

			// Daten in Puffer kopieren
			_data.CopyTo(_pos, buffer, 0, length);

			// Aktuelle Pufferposition erhöhen
			_pos += length;

			// Datenlänge zurückgeben
			return length;
		}

		/// <summary>
		/// Interne Hilfsfunktion. Liest length Bytes aus dem Puffer und schreibt diese in das interne Hilfsarray, verhält sich ansonsten wie die öffentliche Read()-Methode. Dies spart Initialisierungen. Wenn nicht so viele Bytes wie gefordert gelesen werden können, wird eine Exception ausgelöst.
		/// </summary>
		/// <param name="length">Die Länge der zu lesenden Daten.</param>
		private void Read(int length)
		{
			// Feststellen, ob noch genug lesbare Daten vorhanden sind
			if(_pos == _data.Count || _pos + length > _data.Count)
			{
				// Es können keine Daten gelesen werden, Exception auslösen
				throw new IndexOutOfRangeException("Es können keine " + length + " Bytes aus dem Puffer gelesen werden: Entweder ist der Puffer leer oder der Lesezeiger am Pufferende angekommen.");
			}

			// Daten in Puffer kopieren
			_data.CopyTo(_pos, _internalHelpBuffer, 0, length);

			// Aktuelle Pufferposition erhöhen
			_pos += length;
		}

		/// <summary>
		/// Liest einen einzelnen Byte-Wert aus dem Puffer.
		/// </summary>
		/// <returns></returns>
		public byte ReadByte()
		{
			// Sicherstellen, dass der Positionswert nicht am Pufferende steht
			if(_pos == _data.Count || _data.Count == 0)
			{
				// Fehler
				throw new IndexOutOfRangeException("Es konnte kein Byte mehr aus dem Puffer gelesen werden: Entweder ist der Puffer leer oder der Lesezeiger am Pufferende angekommen.");
			}

			// Wert zurückgeben und Position erhoehen
			return _data[_pos++];
		}

		/// <summary>
		/// Schreibt die angegebenen Daten an der aktuellen Position in den Puffer.
		/// </summary>
		/// <param name="data">Die zu schreibenden Daten.</param>
		/// <param name="overWrite">Legt fest, ob die Daten im Puffer mit den neuen Daten überschrieben werden sollen.</param>
		public void Write(byte[] data, bool overWrite = true)
		{
			// Datenlänge merken
			int len = data.Length;

			// Bei großen List-Allokationen kann eine OutOfMemory-Exception auftreten, wenn kein hinreichend großer virtueller Speicherblock gefunden wird
			try
			{
				// Soll überschrieben werden? => Wenn _pos an das Ende des Arrays zeigt, wird in jedem Fall nur eingefügt
				if(overWrite && _pos < _data.Count)
				{
					// Es muss ein bestimmter Bereich gelöscht werden; der Löschbereich darf die Puffergrenze nicht überschreiten
					if(_pos + len < _data.Count - 1)
					{
						// Alles OK, Bereich löschen
						_data.RemoveRange(_pos, len);

						// Daten einfügen
						_data.InsertRange(_pos, data);
					}
					else
					{
						// Nur bis zum Listenende löschen
						_data.RemoveRange(_pos, _data.Count - _pos);

						// Daten anhängen
						_data.AddRange(data);
					}
				}
				else
				{
					// Daten anhängen
					_data.AddRange(data);
				}
			}
			catch(OutOfMemoryException)
			{
				// Fehlermeldung ausgeben
				MessageBox.Show("Schwerwiegender Fehler: Es kann kein hinreichend großer virtueller Speicherblock allokiert werden.\nVersuchen Sie ungespeicherte Änderungen zu sichern und starten Sie das Programm neu, da möglicherweise ein instabiler Zustand besteht.\n\nVielleicht hilft es, das Programm im 64-Bit-Modus auszuführen?", "Fehler bei Allokation", MessageBoxButtons.OK, MessageBoxIcon.Error);

				// Puffer leeren, damit die Meldung nicht noch x-mal auftaucht - der Schaden ist eh schon angerichtet
				_data.Clear();
			}

			// Die Position um die Datenlänge erhöhen
			_pos += len;
		}

		/// <summary>
		/// Schreibt die Daten aus dem gegebenen Puffer in diese Instanz.
		/// </summary>
		/// <param name="buffer">Der Puffer mit den zu schreibenden Daten.</param>
		/// <param name="overWrite">Legt fest, ob die Daten im Puffer mit den neuen Daten überschrieben werden sollen.</param>
		public void Write(RAMBuffer buffer, bool overWrite = true)
		{
			// Datenlänge merken
			int len = buffer.Length;

			// Bei großen List-Allokationen kann eine OutOfMemory-Exception auftreten, wenn kein hinreichend großer virtueller Speicherblock gefunden wird
			try
			{
				// Soll überschrieben werden? => Wenn _pos an das Ende des Arrays zeigt, wird in jedem Fall nur eingefügt
				if(overWrite && _pos < _data.Count)
				{
					// Es muss ein bestimmter Bereich gelöscht werden; der Löschbereich darf die Puffergrenze nicht überschreiten
					if(_pos + len < _data.Count - 1)
					{
						// Alles OK, Bereich löschen
						_data.RemoveRange(_pos, len);

						// Daten einfügen
						_data.InsertRange(_pos, buffer._data);
					}
					else
					{
						// Nur bis zum Listenende löschen
						_data.RemoveRange(_pos, _data.Count - _pos);

						// Daten anhängen
						_data.AddRange(buffer._data);
					}
				}
				else
				{
					// Daten anhängen
					_data.AddRange(buffer._data);
				}
			}
			catch(OutOfMemoryException)
			{
				// Fehlermeldung ausgeben
				MessageBox.Show("Schwerwiegender Fehler: Es kann kein hinreichend großer virtueller Speicherblock allokiert werden.\nVersuchen Sie ungespeicherte Änderungen zu sichern und starten Sie das Programm neu, da möglicherweise ein instabiler Zustand besteht.\n\nVielleicht hilft es, das Programm im 64-Bit-Modus auszuführen?", "Fehler bei Allokation", MessageBoxButtons.OK, MessageBoxIcon.Error);

				// Puffer leeren, damit die Meldung nicht noch x-mal auftaucht - der Schaden ist eh schon angerichtet
				_data.Clear();
			}

			// Die Position um die Datenlänge erhöhen
			_pos += len;
		}

		/// <summary>
		/// Löscht alle enthaltenen Daten und setzt alle Statusvariablen zurück.
		/// </summary>
		public void Clear()
		{
			// Datenarray löschen
			_data.Clear();

			// Position zurücksetzen
			_pos = 0;
		}

		/// <summary>
		/// Schreibt alle Pufferinhalte in die angegebene Datei (bestehende Dateien werden überschrieben).
		/// </summary>
		/// <param name="filename">Die Zieldatei für die Pufferinhalte.</param>
		public void Save(string filename)
		{
			// Datei öffnen
			using(BinaryWriter w = new BinaryWriter(File.Open(filename, FileMode.Create, FileAccess.Write, FileShare.Write)))
			{
				// Daten schreiben
				w.Write(_data.ToArray<byte>());

				// Stream schließen
				w.Close();
			}
		}

		/// <summary>
		/// Gibt den internen Puffer als MemoryStream zurück.
		/// </summary>
		/// <returns></returns>
		public MemoryStream ToMemoryStream()
		{
			// Puffer zurückgeben
			return new MemoryStream(_data.ToArray());
		}

		/// <summary>
		/// Erstellt eine Kopie des aktuellen Objekts und gibt diese zurück.
		/// </summary>
		/// <returns></returns>
		public object Clone()
		{
			return this.MemberwiseClone();
		}

		#region Konvertierende Hilfsfunktionen

		#region Lesen

		/// <summary>
		/// Liest ein Byte-Array der angegebenen Länge ab der aktuellen Position aus dem Puffer.
		/// </summary>
		/// <param name="len">Die Länge des zu lesenden Byte-Arrays.</param>
		/// <returns></returns>
		public byte[] ReadByteArray(int len)
		{
			// Puffer-Byte-Array
			byte[] buffer;

			// Lesen
			Read(out buffer, len);

			// Byte-Array zurückgeben
			return buffer;
		}

		/// <summary>
		/// Liest einen Integer-Wert aus dem Puffer.
		/// </summary>
		/// <returns></returns>
		public int ReadInteger()
		{
			// Bytes lesen und konvertierten Wert zurückgeben
			Read(4);
			return BitConverter.ToInt32(_internalHelpBuffer, 0);
		}

		/// <summary>
		/// Liest einen vorzeichenlosen Integer-Wert aus dem Puffer.
		/// </summary>
		/// <returns></returns>
		public uint ReadUInteger()
		{
			// Bytes lesen und konvertierten Wert zurückgeben
			Read(4);
			return BitConverter.ToUInt32(_internalHelpBuffer, 0);
		}

		/// <summary>
		/// Liest einen Short-Wert aus dem Puffer.
		/// </summary>
		/// <returns></returns>
		public short ReadShort()
		{
			// Bytes lesen und konvertierten Wert zurückgeben
			Read(2);
			return BitConverter.ToInt16(_internalHelpBuffer, 0);
		}

		/// <summary>
		/// Liest einen vorzeichenlosen Short-Wert aus dem Puffer.
		/// </summary>
		/// <returns></returns>
		public ushort ReadUShort()
		{
			// Bytes lesen und konvertierten Wert zurückgeben
			Read(2);
			return BitConverter.ToUInt16(_internalHelpBuffer, 0);
		}

		/// <summary>
		/// Liest einen Long-Wert aus dem Puffer.
		/// </summary>
		/// <returns></returns>
		public long ReadLong()
		{
			// Bytes lesen und konvertierten Wert zurückgeben
			Read(8);
			return BitConverter.ToInt64(_internalHelpBuffer, 0);
		}

		/// <summary>
		/// Liest einen vorzeichenlosen Long-Wert aus dem Puffer.
		/// </summary>
		/// <returns></returns>
		public ulong ReadULong()
		{
			// Bytes lesen und konvertierten Wert zurückgeben
			Read(8);
			return BitConverter.ToUInt64(_internalHelpBuffer, 0);
		}

		/// <summary>
		/// Liest einen Float-Wert aus dem Puffer.
		/// </summary>
		/// <returns></returns>
		public float ReadFloat()
		{
			// Bytes lesen und konvertierten Wert zurückgeben
			Read(4);
			return BitConverter.ToSingle(_internalHelpBuffer, 0);
		}

		/// <summary>
		/// Liest einen Double-Wert aus dem Puffer.
		/// </summary>
		/// <returns></returns>
		public double ReadDouble()
		{
			// Bytes lesen und konvertierten Wert zurückgeben
			Read(8);
			return BitConverter.ToDouble(_internalHelpBuffer, 0);
		}

		/// <summary>
		/// Liest eine ANSI-Zeichenkette angegebener Länge aus dem Puffer.
		/// </summary>
		/// <param name="length">Die Länge der zu lesenden Zeichenkette.</param>
		/// <returns></returns>
		public string ReadString(int length)
		{
			// Wert zurückgeben
			return System.Text.Encoding.Default.GetString(ReadByteArray(length));
		}

		#endregion Lesen

		#region Schreiben

		/// <summary>
		/// Schreibt einen Byte-Wert in den Puffer.
		/// </summary>
		/// <param name="value">Der zu schreibende Wert.</param>
		public void WriteByte(byte value)
		{
			// Wert schreiben
			Write(new byte[] { value });
		}

		/// <summary>
		/// Schreibt einen Float-Wert in den Puffer.
		/// </summary>
		/// <param name="value">Der zu schreibende Wert.</param>
		public void WriteFloat(float value)
		{
			// Wert schreiben
			Write(BitConverter.GetBytes(value));
		}

		/// <summary>
		/// Schreibt einen Integer-Wert in den Puffer.
		/// </summary>
		/// <param name="value">Der zu schreibende Wert.</param>
		public void WriteInteger(int value)
		{
			// Wert schreiben
			Write(BitConverter.GetBytes(value));
		}

		/// <summary>
		/// Schreibt einen vorzeichenlosen Integer-Wert in den Puffer.
		/// </summary>
		/// <param name="value">Der zu schreibende Wert.</param>
		public void WriteUInteger(uint value)
		{
			// Wert schreiben
			Write(BitConverter.GetBytes(value));
		}

		/// <summary>
		/// Schreibt einen Short-Wert in den Puffer.
		/// </summary>
		/// <param name="value">Der zu schreibende Wert.</param>
		public void WriteShort(short value)
		{
			// Wert schreiben
			Write(BitConverter.GetBytes(value));
		}

		/// <summary>
		/// Schreibt einen vorzeichenlosen Short-Wert in den Puffer.
		/// </summary>
		/// <param name="value">Der zu schreibende Wert.</param>
		public void WriteUShort(ushort value)
		{
			// Wert schreiben
			Write(BitConverter.GetBytes(value));
		}

		/// <summary>
		/// Schreibt einen Long-Wert in den Puffer.
		/// </summary>
		/// <param name="value">Der zu schreibende Wert.</param>
		public void WriteLong(long value)
		{
			// Wert schreiben
			Write(BitConverter.GetBytes(value));
		}

		/// <summary>
		/// Schreibt einen vorzeichenlosen Long-Wert in den Puffer.
		/// </summary>
		/// <param name="value">Der zu schreibende Wert.</param>
		public void WriteULong(ulong value)
		{
			// Wert schreiben
			Write(BitConverter.GetBytes(value));
		}

		/// <summary>
		/// Schreibt einen Float-Wert in den Puffer.
		/// </summary>
		/// <param name="value">Der zu schreibende Wert.</param>
		public void WriteLong(float value)
		{
			// Wert schreiben
			Write(BitConverter.GetBytes(value));
		}

		/// <summary>
		/// Schreibt einen Double-Wert in den Puffer.
		/// </summary>
		/// <param name="value">Der zu schreibende Wert.</param>
		public void WriteLong(double value)
		{
			// Wert schreiben
			Write(BitConverter.GetBytes(value));
		}

		/// <summary>
		/// Schreibt eine ANSI-Zeichenkette in den Puffer.
		/// </summary>
		/// <param name="value">Die zu schreibende Zeichenkette.</param>
		public void WriteString(string value)
		{
			// Zeichenkette schreiben
			Write(System.Text.Encoding.Default.GetBytes(value));
		}

		/// <summary>
		/// Schreibt eine ANSI-Zeichenkett der gegebenen Länge in den Puffer. Falls der String kürzer ist als angegeben, wird der Rest mit 0-Bytes aufgefüllt.
		/// </summary>
		/// <param name="value">Der zu schreibende String.</param>
		/// <param name="length">Die Soll-Länge des zu schreibenden Strings.</param>
		public void WriteString(string value, int length)
		{
			// Byte-Array anlegen
			byte[] val = new byte[length];

			// String in Byte-Array kopieren, dabei maximal length Zeichen berücksichtigen
			Encoding.Default.GetBytes(value.Substring(0, Math.Min(length, value.Length))).CopyTo(val, 0);

			// Wert in den Puffer schreiben
			Write(val);
		}

		#endregion Schreiben

		#endregion Konvertierende Hilfsfunktionen

		#region Eigenschaften

		/// <summary>
		/// Ruft die aktuelle Pufferposition ab oder legt diese fest.
		/// </summary>
		public int Position
		{
			get
			{
				// Position zurückgeben
				return _pos;
			}
			set
			{
				// Position überprüfen und ggf. Fehler auslösen
				if(value <= _data.Count)
					_pos = value;
				else
					throw new ArgumentException("Die angegebene Position liegt nicht innerhalb der Daten!");
			}
		}

		/// <summary>
		/// Ruft die aktuelle Puffergröße ab.
		/// </summary>
		public int Length
		{
			get
			{
				// Pufferlänge zurückgeben
				return _data.Count;
			}
		}

		#endregion Eigenschaften
	}
}