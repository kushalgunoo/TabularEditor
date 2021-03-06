﻿using System;
using System.Linq;
using System.Collections.Generic;
using TabularEditor.PropertyGridUI;
using TOM = Microsoft.AnalysisServices.Tabular;
using System.Diagnostics;
using System.ComponentModel;
using TabularEditor.TOMWrapper.Undo;
using System.ComponentModel.Design;
using System.Drawing.Design;

namespace TabularEditor.TOMWrapper
{
    public partial class Partition: IExpressionObject
    {
        public bool NeedsValidation { get { return false; } set { } }
        internal static void CreateCalculatedTablePartition(CalculatedTable calcTable)
        {
            var tomPartition = new TOM.Partition();
            tomPartition.Name = calcTable.Name;
            var partition = new Partition(tomPartition);

            calcTable.Partitions.Add(partition);
            partition.Init();
            partition.MetadataObject.Source = new TOM.CalculatedPartitionSource();
        }

        protected override void Init()
        {
            if (MetadataObject.Source == null && !(Parent is CalculatedTable))
            {
                if (Model.DataSources.Count == 0) Model.AddDataSource();
                MetadataObject.Source = new TOM.QueryPartitionSource()
                {
                    DataSource = Model.DataSources.Any(ds => ds is ProviderDataSource) ?
                        Model.DataSources.First(ds => ds is ProviderDataSource).MetadataObject :
                        Model.DataSources.First().MetadataObject
                };
            }
            base.Init();
        }

        [Category("Basic"),Description("The query which is executed on the Data Source to populate this partition with data.")]
        [Editor(typeof(MultilineStringEditor), typeof(UITypeEditor)), IntelliSense("Gets or sets the query which is executed on the Data Source to populate the partition with data.")]
        public string Query { get { return Expression; } set { Expression = value; } }

        [Category("Expression"), Description("The expression which is used to populate this partition with data.")]
        [Editor(typeof(MultilineStringEditor), typeof(UITypeEditor))]
        public string Expression
        {
            get
            {
                switch(MetadataObject.SourceType)
                {
                    case TOM.PartitionSourceType.Calculated:
                        return (MetadataObject.Source as TOM.CalculatedPartitionSource)?.Expression;
                    case TOM.PartitionSourceType.Query:
                        return (MetadataObject.Source as TOM.QueryPartitionSource)?.Query;
                    case TOM.PartitionSourceType.M:
                        return (MetadataObject.Source as TOM.MPartitionSource)?.Expression;
                    default:
                        return null;
                }
            }
            set
            {
                var oldValue = Expression;
                if (oldValue == value) return;
                bool undoable = true;
                bool cancel = false;
                OnPropertyChanging("Expression", value, ref undoable, ref cancel);
                if (cancel) return;

                switch (MetadataObject.SourceType)
                {
                    case TOM.PartitionSourceType.Calculated:
                        (MetadataObject.Source as TOM.CalculatedPartitionSource).Expression = value; break;
                    case TOM.PartitionSourceType.Query:
                        (MetadataObject.Source as TOM.QueryPartitionSource).Query = value; break;
                    case TOM.PartitionSourceType.M:
                        (MetadataObject.Source as TOM.MPartitionSource).Expression = value; break;
                    default:
                        throw new NotSupportedException();
                }

                if (undoable) Handler.UndoManager.Add(new UndoPropertyChangedAction(this, "Expression", oldValue, value));
                OnPropertyChanged("Expression", oldValue, value);
            }
        }

        [Browsable(false)]
        public ProviderDataSource ProviderDataSource => DataSource as ProviderDataSource;
        [Browsable(false)]
        public StructuredDataSource StructuredDataSource => DataSource as StructuredDataSource;

        [Category("Other"),DisplayName("Data Source"),Description("The Data Source used by this partition."),TypeConverter(typeof(DataSourceConverter))]
        public DataSource DataSource
        {
            get
            {
                if (MetadataObject.Source is TOM.QueryPartitionSource)
                {
                    var ds = (MetadataObject.Source as TOM.QueryPartitionSource)?.DataSource;
                    return ds == null ? null : Handler.WrapperLookup[ds] as DataSource;
                }
                else return null;
            }
            set
            {
                if (MetadataObject.Source is TOM.QueryPartitionSource)
                {
                    if (value == null) return;
                    var oldValue = DataSource;
                    if (oldValue == value) return;
                    bool undoable = true;
                    bool cancel = false;
                    OnPropertyChanging("DataSource", value, ref undoable, ref cancel);
                    if (cancel) return;
                    (MetadataObject.Source as TOM.QueryPartitionSource).DataSource = value?.MetadataObject;
                    if (undoable) Handler.UndoManager.Add(new UndoPropertyChangedAction(this, "DataSource", oldValue, value));
                    OnPropertyChanged("DataSource", oldValue, value);
                }
            }
        }

        internal override bool IsBrowsable(string propertyName)
        {
            switch(propertyName)
            {
                case Properties.CUBENAME:
                    return Handler.CompatibilityLevel >= 1510;
                case "DataSource":
                case "Query":
                    return SourceType == PartitionSourceType.Query;
                case Properties.EXPRESSION:
                    return SourceType == PartitionSourceType.Calculated || SourceType == PartitionSourceType.M;
                case Properties.MODE:
                case Properties.DATAVIEW:
                case Properties.DESCRIPTION:
                case Properties.NAME:
                case Properties.REFRESHEDTIME:
                case Properties.OBJECTTYPENAME:
                case Properties.STATE:
                case Properties.SOURCETYPE:
                case Properties.ANNOTATIONS:
                    return true;
                default:
                    return false;
            }
        }

        [Category("Metadata"),DisplayName("Last Processed")]
        public DateTime RefreshedTime
        {
            get { return MetadataObject.RefreshedTime; }
        }

        public override string Name
        {
            set
            {
                base.Name = value;
            }
            get
            {
                return base.Name;
            }
        }

        protected override bool AllowDelete(out string message)
        {
            if(Table.Partitions.Count == 1)
            {
                message = Messages.TableMustHaveAtLeastOnePartition;
                return false;
            }
            return base.AllowDelete(out message);
        }

        internal override bool Editable(string propertyName)
        {
            switch(propertyName)
            {
                case "Name":
                case "Description":
                case "DataSource":
                case "Query":
                case "Expression":
                case "Mode":
                case "DataView":
                case "Annotations":
                    return true;
                default:
                    return false;
            }
        }
    }

    public class MPartition: Partition
    {
        public override Partition Clone(string newName = null, Table newParent = null)
        {
            if (TabularModelHandler.Singleton.UsePowerBIGovernance && !PowerBI.PowerBIGovernance.AllowCreate(typeof(Partition)))
            {
                throw new InvalidOperationException(string.Format(Messages.CannotCreatePowerBIObject, typeof(Partition).GetTypeName()));
            }

            Handler.BeginUpdate("Clone Partition");

            // Create a clone of the underlying metadataobject:
            var tom = MetadataObject.Clone() as TOM.Partition;


            // Assign a new, unique name:
            tom.Name = Parent.Partitions.GetNewName(string.IsNullOrEmpty(newName) ? tom.Name + " copy" : newName);

            // Create the TOM Wrapper object, representing the metadataobject
            MPartition obj = CreateFromMetadata(newParent ?? Parent, tom);

            Handler.EndUpdate();

            return obj;
        }

        protected override void Init()
        {
            if (MetadataObject.Source == null && !(Parent is CalculatedTable))
            {
                if (Model.DataSources.Count == 0) StructuredDataSource.CreateNew(Model);
                MetadataObject.Source = new TOM.MPartitionSource();
            }
            base.Init();
        }

        protected MPartition(TOM.Partition metadataObject) : base(metadataObject)
        {
        }

        public new static MPartition CreateNew(Table parent, string name = null)
        {
            if (TabularModelHandler.Singleton.UsePowerBIGovernance && !PowerBI.PowerBIGovernance.AllowCreate(typeof(MPartition)))
            {
                throw new InvalidOperationException(string.Format(Messages.CannotCreatePowerBIObject, typeof(MPartition).GetTypeName()));
            }

            var metadataObject = new TOM.Partition();
            metadataObject.Name = parent.Partitions.GetNewName(string.IsNullOrWhiteSpace(name) ? "New " + typeof(MPartition).GetTypeName() : name);
            metadataObject.Source = new TOM.MPartitionSource();

            var obj = new MPartition(metadataObject);

            parent.Partitions.Add(obj);

            obj.Init();

            return obj;

        }

        internal new static MPartition CreateFromMetadata(Table parent, TOM.Partition metadataObject)
        {
            var obj = new MPartition(metadataObject);
            parent.Partitions.Add(obj);

            obj.Init();

            return obj;
        }
    }

    public partial class PartitionCollection : ITabularNamedObject, ITabularObjectContainer, ITabularTableObject
    {
        bool ITabularNamedObject.CanEditName() { return false; }

        [IntelliSense("Converts all M partitions in this collection to regular partitions. The M query is left as-is and needs to be converted to SQL before the partition can be processed.")]
        public void ConvertToLegacy(ProviderDataSource providerSource = null)
        {
            Handler.BeginUpdate("Convert partitions");
            foreach(var oldPartition in this.OfType<MPartition>().ToList())
            {
                var newPartition = Partition.CreateNew(Table);
                newPartition.DataSource = providerSource == null ? oldPartition.DataSource : providerSource;
                newPartition.Expression = oldPartition.Expression;

                oldPartition.Delete();
                newPartition.Name = oldPartition.Name;
            }
            Handler.EndUpdate();
        }

        [IntelliSense("Converts all provider source partitions in this collection to M partitions. The provider query is left as-is and needs to be converted to an M query before the partition can be processed.")]
        public void ConvertToPowerQuery()
        {
            Handler.BeginUpdate("Convert partitions");
            foreach (var oldPartition in this.Where(p => p.GetType() == typeof(Partition)).ToList())
            {
                var newPartition = MPartition.CreateNew(Table);
                newPartition.DataSource = oldPartition.DataSource;
                newPartition.Expression = oldPartition.Query;

                oldPartition.Delete();
                newPartition.Name = oldPartition.Name;
            }
            Handler.EndUpdate();
        }

        /// <summary>
        /// This property points to the PartitionCollection itself. It is used only to display a clickable
        /// "Partitions" property in the Property Grid, which will open the PartitionCollectionEditor when
        /// clicked.
        /// </summary>
        [DisplayName("Partitions"),Description("The collection of Partition objects on this Table.")]
        [Category("Data Source"), IntelliSense("The collection of Partition objects on this Table.")]
        [NoMultiselect(), Editor(typeof(PartitionCollectionEditor), typeof(UITypeEditor))]
        public PartitionCollection PropertyGridPartitions => this;

        bool ITabularObject.IsRemoved => false;

        int ITabularNamedObject.MetadataIndex => -1;

        Model ITabularObject.Model => Table.Model;

        [ReadOnly(true)]
        string ITabularNamedObject.Name
        {
            get
            {
                return "Partitions";
            }
            set
            {

            }
        }

        ObjectType ITabularObject.ObjectType => ObjectType.PartitionCollection;

        Table ITabularTableObject.Table => Table;

        event PropertyChangedEventHandler INotifyPropertyChanged.PropertyChanged
        {
            add
            {
                throw new NotImplementedException();
            }

            remove
            {
                throw new NotImplementedException();
            }
        }

        bool ITabularNamedObject.CanDelete() => false;

        bool ITabularNamedObject.CanDelete(out string message)
        {
            message = Messages.CannotDeleteObject;
            return false;
        }

        void ITabularNamedObject.Delete()
        {
            throw new NotSupportedException();
        }

        IEnumerable<ITabularNamedObject> ITabularObjectContainer.GetChildren()
        {
            return this;
        }
    }
}
