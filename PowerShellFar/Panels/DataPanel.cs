
/*
PowerShellFar module for Far Manager
Copyright (c) 2006 Roman Kuzmin
*/

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Management.Automation;
using FarNet;

namespace PowerShellFar
{
	/// <summary>
	/// Panel exploring a data table.
	/// </summary>
	public sealed class DataPanel : TablePanel, IDisposable
	{
		/// <summary>
		/// Constructor.
		/// </summary>
		public DataPanel()
		{
			PanelDirectory = "*";

			UseFilter = true;
			UseSortGroups = false;

			// modes: assume it is sorted in SELECT
			SortMode = PanelSortMode.Unsorted;

			Closed += OnClosed;

			FileComparer  = new FileDataComparer();
		}

		/// <summary>
		/// Database provider factory instance.
		/// See <b>System.Data.Common.DbProviderFactories</b> methods <b>GetFactoryClasses</b>, <b>GetFactory</b>.
		/// </summary>
		public DbProviderFactory Factory { get; set; }

		/// <summary>
		/// Data adapter.
		/// You have to set it and configure at least its <c>SelectCommand</c>.
		/// </summary>
		public DbDataAdapter Adapter { get; set; }

		/// <summary>
		/// A table which records are used as panel items.
		/// </summary>
		/// <remarks>
		/// Normally this table is created, assigned and filled internally.
		/// An external table is possible but this scenario is mostly reserved for the future.
		/// </remarks>
		public DataTable Table { get; set; }

		/// <summary>
		/// Connection.
		/// </summary>
		public DbConnection Connection
		{
			get { return Adapter == null || Adapter.SelectCommand == null ? null : Adapter.SelectCommand.Connection; }
		}

		readonly DataRowFileMap Map = new DataRowFileMap();

		/// <summary>
		/// Disposes internal data. Normally it is called internally.
		/// </summary>
		public void Dispose()
		{
			if (_Builder != null)
				_Builder.Dispose();

			if (Table != null)
				Table.Dispose();
		}

		/// <summary>
		/// Calls <c>Adapter.Update()</c>.
		/// </summary>
		public override bool SaveData()
		{
			// Build commands
			BuildDeleteCommand();
			BuildInsertCommand();
			BuildUpdateCommand();

			try
			{
				ToUpdateData = true;
				Adapter.Update(Table);
				return true;
			}
			catch (DBConcurrencyException ex)
			{
				A.Msg(ex);
			}
			catch (DbException ex)
			{
				A.Msg(ex);
			}
			return false;
		}

		///
		protected override string DefaultTitle { get { return string.IsNullOrEmpty(Table.TableName) ? "Data Table" : "Table " + Table.TableName; } }

		/// <summary>
		/// Fills data table and shows the panel.
		/// </summary>
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity")]
		public override void Open()
		{
			if (IsOpened)
				return;

			if (Table == null && Adapter == null)
				throw new RuntimeException("The table and the adapter are null.");
			if (Table == null && Adapter.SelectCommand == null)
				throw new RuntimeException("The table and the adapter select command are null.");

			// create and fill table
			if (Table == null)
			{
				Table = new DataTable();
				Table.Locale = CultureInfo.CurrentCulture; // CA
				Fill();
			}

			// pass 1: collect the columns
			IList<Meta> metas;
			if (Columns == null)
			{
				// collect all table columns skipping not linear data types
				int Count = Math.Min(Table.Columns.Count, A.Psf.Settings.MaximumPanelColumnCount);
				metas = new List<Meta>(Count);
				int nCollected = 0;
				foreach (DataColumn column in Table.Columns)
				{
					if (Converter.IsLinearType(column.DataType))
					{
						Meta meta = new Meta(column.ColumnName);
						meta.Kind = FarColumn.DefaultColumnKinds[nCollected];
						metas.Add(meta);
						++nCollected;
						if (nCollected >= Count)
							break;
					}
				}
			}
			else
			{
				// setup user defined columns
				metas = SetupColumns(Columns);
			}

			// at least one column
			if (metas.Count == 0)
				throw new InvalidOperationException("There is no column to display.");

			// pass 2: mapping
			foreach (Meta meta in metas)
			{
				DataColumn column = Table.Columns[meta.Property];

				switch (meta.Kind[0])
				{
					case 'N':
						Map.Name = column.Ordinal;
						break;
					case 'O':
						Map.Owner = column.Ordinal;
						break;
					case 'Z':
						Map.Description = column.Ordinal;
						break;
					case 'C':
						Map.Columns.Add(column.Ordinal);
						break;
					case 'S':
						{
							if (Map.Length >= 0)
								throw new InvalidOperationException("Column 'S' is used twice.");
							Map.Length = column.Ordinal;
						}
						break;
					case 'D':
						{
							if (meta.Kind.Length < 2)
								throw new InvalidOperationException(Res.InvalidColumnKind + "D");

							switch (meta.Kind[1])
							{
								case 'C':
									{
										if (Map.CreationTime >= 0)
											throw new InvalidOperationException("Column 'DC' is used twice.");

										Map.CreationTime = column.Ordinal;
									}
									break;
								case 'M':
									{
										if (Map.LastWriteTime >= 0)
											throw new InvalidOperationException("Column 'DM' is used twice.");

										Map.LastWriteTime = column.Ordinal;
									}
									break;
								case 'A':
									{
										if (Map.LastAccessTime >= 0)
											throw new InvalidOperationException("Column 'DA' is used twice.");

										Map.LastAccessTime = column.Ordinal;
									}
									break;
								default:
									throw new InvalidOperationException(Res.InvalidColumnKind + meta.Kind);
							}
						}
						break;
					default:
						throw new InvalidOperationException(Res.InvalidColumnKind + meta.Kind);
				}
			}

			// pass 3: set plan
			SetPlan(PanelViewMode.AlternativeFull, SetupPanelMode(metas));

			base.Open();
		}

		///??
		protected override bool CanClose()
		{
			using (DataTable dt = Table.GetChanges())
			{
				if (dt == null)
					return true;
			}

			switch (Far.Net.Message(Res.AskSaveModified, "Save", MsgOptions.YesNoCancel))
			{
				case 0:
					return SaveData();
				case 1:
					Table.RejectChanges(); // to avoid request
					return true;
				default:
					return false;
			}
		}

		///??
		protected override bool CanCloseChild()
		{
			MemberPanel mp = Child as MemberPanel;
			if (mp == null)
				return true;

			DataRow dr = mp.Value.BaseObject as DataRow;
			if (dr == null)
				return true;

			if (0 == (dr.RowState & (DataRowState.Added | DataRowState.Deleted | DataRowState.Modified)))
				return true;

			switch (Far.Net.Message(Res.AskSaveModified, "Save", MsgOptions.YesNoCancel))
			{
				case 0:
					return SaveData();
				case 1:
					dr.RejectChanges();
					return true;
				default:
					return false;
			}
		}

		internal override void DoDeleteFiles(FilesEventArgs args)
		{
			BuildDeleteCommand();

			if ((Far.Net.Confirmations & FarConfirmations.Delete) != 0)
			{
				if (Far.Net.Message("Delete selected record(s)?", Res.Delete, MsgOptions.None, new string[] { Res.Delete, Res.Cancel }) != 0)
					return;
			}

			ToUpdateData = true;

			foreach (FarFile f in args.Files)
			{
				DataRow dr = f.Data as DataRow;
				if (dr == null)
				{
					Files.Remove(f);
					continue;
				}

				bool ok = true;
				try
				{
					dr.Delete();
					ok = SaveData();
				}
				catch
				{
					ok = false;
					throw;
				}
				finally
				{
					if (!ok)
					{
						dr.RejectChanges();
						PostData(dr);
					}
				}
				if (!ok)
					break;
			}
		}

		bool __toUpdateData = true;
		bool ToUpdateData
		{
			get { return __toUpdateData; }
			set { __toUpdateData = value; }
		}

		internal override void OnUpdateFiles(PanelEventArgs e)
		{
			// refill
			if (UserWants == UserAction.CtrlR && Adapter != null)
			{
				if (CanClose())
				{
					Table.Clear();
					Fill();
				}
			}

			// no job?
			if (!ToUpdateData && UserWants != UserAction.CtrlR)
				return;

			// refresh data
			for (int iFile = Files.Count; --iFile >= 0; )
			{
				FarFile f = Files[iFile];
				DataRow Row = f.Data as DataRow;
				if (Row == null || Row.RowState == DataRowState.Deleted || Row.RowState == DataRowState.Detached)
				{
					Files.RemoveAt(iFile);
					continue;
				}
			}

			// prevent next job
			ToUpdateData = false;
		}

		/// <summary>
		/// Opens a member panel to edit the record.
		/// </summary>
		public override void OpenFile(FarFile file)
		{
			OpenFileMembers(file);
		}

		void Fill()
		{
			Adapter.Fill(Table);

			Files = new List<FarFile>(Table.Rows.Count + 1);
			foreach (DataRow dr in Table.Rows)
				Files.Add(new DataRowFile(dr, Map));
		}

		/// <summary>
		/// Core handler.
		/// </summary>
		void OnClosed(object sender, EventArgs e)
		{
			if (Adapter != null)
			{
				using (DataTable dt = Table.GetChanges())
				{
					if (dt != null && Far.Net.Message(Res.AskSaveModified, "Save", MsgOptions.YesNo) == 0)
						SaveData();
				}
			}
			Dispose();
		}

		internal override void UICreate()
		{
			BuildInsertCommand();

			// add new row to the table
			DataRow dr = Table.NewRow();
			Table.Rows.Add(dr);

			// add new file to the panel and go to it
			DataRowFile f = new DataRowFile(dr, Map);
			Files.Add(f);
			PostFile(f);
			ToUpdateData = true;
			UpdateRedraw(true);

			// open the record panel
			OpenFile(f);
		}

		internal override void ShowHelp()
		{
			Help.ShowTopic("DataPanel");
		}

		// Command builder
		DbCommandBuilder _Builder;

		void EnsureBuilder()
		{
			if (_Builder != null)
				return;

			if (Factory == null)
				throw new RuntimeException("Cannot create a command builder because this.Factory is null.");

			if (Adapter == null)
				Adapter = Factory.CreateDataAdapter();

			_Builder = Factory.CreateCommandBuilder();
			_Builder.DataAdapter = Adapter;
		}

		/// <summary>
		/// Builds DELETE command.
		/// </summary>
		public void BuildDeleteCommand()
		{
			if (Adapter != null && Adapter.DeleteCommand != null)
				return;

			EnsureBuilder();
			Adapter.DeleteCommand = _Builder.GetDeleteCommand();
		}

		/// <summary>
		/// Builds INSERT command.
		/// </summary>
		public void BuildInsertCommand()
		{
			if (Adapter != null && Adapter.InsertCommand != null)
				return;

			EnsureBuilder();
			Adapter.InsertCommand = _Builder.GetInsertCommand();
		}

		/// <summary>
		/// Builds UPDATE command.
		/// </summary>
		public void BuildUpdateCommand()
		{
			if (Adapter != null && Adapter.UpdateCommand != null)
				return;

			EnsureBuilder();
			Adapter.UpdateCommand = _Builder.GetUpdateCommand();
		}

		///
		internal override void HelpMenuInitItems(HelpMenuItems items, PanelMenuEventArgs e)
		{
			if (items.Create == null)
				items.Create = new SetItem()
				{
					Text = "&New row",
					Click = delegate { UICreate(); }
				};

			if (items.Delete == null)
				items.Delete = new SetItem()
				{
					Text = "&Delete row(s)",
					Click = delegate { UIDelete(false); }
				};

			base.HelpMenuInitItems(items, e);
		}
		internal override string HelpMenuTextOpenFileMembers { get { return "Edit row data"; } }

	}
}
