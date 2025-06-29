using DiffusionNexus.LoraSort.Service.Classes;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Xml.Serialization;

namespace DiffusionNexus.LoraSort.Service.Services
{
    public class CustomTagMapXmlService
    {
        string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mappings.xml");
        /// <summary>
        /// Saves the given collection of CustomTagMap objects to an XML file.
        /// </summary>
        /// <param name="mappings">The observable collection of mappings to save.</param>
        /// <param name="filePath">The full file path where the XML should be saved.</param>
        public void SaveMappings(ObservableCollection<CustomTagMap> mappings)
        {
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(ObservableCollection<CustomTagMap>));
                using (StreamWriter writer = new StreamWriter(filePath))
                {
                    serializer.Serialize(writer, mappings);
                }
            }
            catch (Exception ex)
            {
                // Handle or log the exception as needed.
                throw new ApplicationException("Error saving mappings to XML.", ex);
            }
        }

        /// <summary>
        /// Loads the collection of CustomTagMap objects from an XML file.
        /// If the file does not exist, an empty collection is returned.
        /// </summary>
        /// <param name="filePath">The full file path from which to load the XML.</param>
        /// <returns>An ObservableCollection of CustomTagMap objects.</returns>
        public ObservableCollection<CustomTagMap> LoadMappings()
        {
            try
            {
                if (!File.Exists(filePath))
                    return new ObservableCollection<CustomTagMap>();

                XmlSerializer serializer = new XmlSerializer(typeof(ObservableCollection<CustomTagMap>));
                using (StreamReader reader = new StreamReader(filePath))
                {
                    return (ObservableCollection<CustomTagMap>)serializer.Deserialize(reader);
                }
            }
            catch (Exception ex)
            {
                // Handle or log the exception as needed.
                Debug.WriteLine("Error loading mappings from XML.", ex);
                return new ObservableCollection<CustomTagMap>();
            }
        }
        /// <summary>
        /// Deletes the mappings XML file if it exists.
        /// </summary>
        /// <param name="filePath">The full path to the mappings XML file.</param>
        public void DeleteAllMappings(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                throw new ApplicationException("Error deleting all mappings.", ex);
            }
        }
    }
}
