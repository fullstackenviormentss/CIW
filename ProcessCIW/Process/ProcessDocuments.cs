﻿using CsvHelper;
using CsvHelper.Configuration;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentValidation.Results;
using MySql.Data.MySqlClient;
using ProcessCIW.Mapping;
using ProcessCIW.Models;
using ProcessCIW.Process;
using ProcessCIW.Validation;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.IO;
using System.Linq;
using System.Xml;

namespace ProcessCIW
{
    public enum ErrorCodes : int
    {
        unknown_error = -1,
        unprocessed=0,
        successfully_processed=1,
        password_protected=-2,
        wrong_version=-3,
        arra=-4,
        duplicate_user=-5,
        failed_validation=-6
    }


/// <summary>
/// Class that controls processing of CIW forms
/// </summary>
class ProcessDocuments
    {
        private static CsvConfiguration config;
        private readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Constructor that sets up CsvConfiguration which is part of CsvHelper
        /// </summary>
        public ProcessDocuments()
        {
            config = new CsvConfiguration();

            config.Delimiter = "||";
            config.HasHeaderRecord = true;
            config.WillThrowOnMissingField = false;
            config.IsHeaderCaseSensitive = false;
            config.TrimFields = false;
        }

        /// <summary>
        /// Gets a list of unprocessed files by calling a stored procedure
        /// </summary>
        /// <returns>List of unprocessed files</returns>
        public List<UnprocessedFiles> GetUnprocessedFiles()
        {
            MySqlCommand cmd = new MySqlCommand();

            List<UnprocessedFiles> uf = new List<UnprocessedFiles>();

            using (MySqlConnection conn = new MySqlConnection(ConfigurationManager.ConnectionStrings["GCIMS"].ToString()))
            {
                conn.Open();

                using (cmd)
                {
                    MySqlDataReader unprocessedFiles;

                    cmd.Connection = conn;
                    cmd.CommandText = "CIW_Unprocessed";
                    cmd.CommandType = CommandType.StoredProcedure;

                    cmd.Parameters.Add("SQLExceptionWarning", MySqlDbType.VarChar, 4000);

                    unprocessedFiles = cmd.ExecuteReader();

                    while (unprocessedFiles.Read())
                    {
                        uf.Add(
                                new UnprocessedFiles
                                {
                                    ID = (int)unprocessedFiles[0],
                                    PersID = (int)unprocessedFiles[1],
                                    FileName = unprocessedFiles[2].ToString()
                                }
                              );
                    }
                }
            }

            log.Info(string.Format("CIW_Unprocessed returned with {0} unprocessed files and SQLExceptionWarning:{1}", uf.Count, cmd.Parameters["SQLExceptionWarning"].Value));

            return uf;
        }

        /// <summary>
        /// Updates upload table after finished processing by calling stored procedure
        /// </summary>
        /// <param name="documentID"></param>
        /// <param name="processedResult"></param>
        public void UpdateProcessed(int documentID, int processedResult)
        {
            MySqlCommand cmd = new MySqlCommand();

            log.Info(string.Format("Updating processed document {0} with result {1}", documentID, processedResult));

            using (MySqlConnection conn = new MySqlConnection(ConfigurationManager.ConnectionStrings["GCIMS"].ToString()))
            {
                conn.Open();

                using (cmd)
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "CIW_UpdateProcessed";
                    cmd.CommandType = CommandType.StoredProcedure;

                    MySqlParameter[] ProcessedParameters = new MySqlParameter[]
                    {
                        new MySqlParameter { ParameterName = "documentID", Value = documentID, MySqlDbType = MySqlDbType.Int32, Direction = ParameterDirection.Input },
                        new MySqlParameter { ParameterName = "processedResult", Value = processedResult, MySqlDbType = MySqlDbType.Int32, Direction = ParameterDirection.Input },
                        new MySqlParameter { ParameterName = "SQLExceptionWarning", MySqlDbType=MySqlDbType.VarChar, Size=4000, Direction = ParameterDirection.Output },
                    };

                    cmd.Parameters.AddRange(ProcessedParameters);

                    cmd.ExecuteNonQuery();

                    log.Info(string.Format("CIW_UpdateProcessed completed and error message: {0}", cmd.Parameters["SQLExceptionWarning"].Value));
                }
            }
        }

        /// <summary>
        /// Get all the CIW information, create temp csv file then load that and then filter it down to the different objects
        /// </summary>
        /// <param name="fileName"></param>

        public string GetCIWInformation(int uploaderID, string filePath, string fileName, out int errorCode)
        {
            List<CIWData> ciwInformation = new List<CIWData>();

            log.Info(String.Format("Getting information from file {0}", filePath));

            //Check for password protection
            try
            {
                using (WordprocessingDocument wd = WordprocessingDocument.Open(filePath, false))
                {
                    DocumentProtection dp = wd.MainDocumentPart.DocumentSettingsPart.Settings.GetFirstChild<DocumentProtection>();
                }
            }
            catch (FileFormatException e)
            {
                log.Error(string.Format("Locked Document - {0} with inner exception:{1}", e.Message, e.InnerException));
                sendPasswordProtection(uploaderID, fileNameHelper(fileName));
                errorCode = (int)ErrorCodes.password_protected;
                log.Error(string.Format("Inserting error code {0}:{1} into upload table", ErrorCodes.password_protected, (int)ErrorCodes.password_protected));
                return null;
            }

            //Begin parsing XML from CIW document
            using (var document = WordprocessingDocument.Open(filePath, true))
            {
                XmlDocument xml = new XmlDocument();
                MainDocumentPart docPart = document.MainDocumentPart;
                xml.InnerXml = docPart.Document.FirstChild.OuterXml;
                XmlNamespaceManager nameSpaceManager = new XmlNamespaceManager(xml.NameTable);
                nameSpaceManager.AddNamespace("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");

                //Get Version number node
                var node = xml.SelectSingleNode(string.Format("w:body/w:tbl/w:tr/w:tc/w:tbl/w:tr/w:tc/w:sdt/w:sdtContent/w:p/w:r/w:t"), nameSpaceManager);

                if (node != null)
                {
                    if (node.InnerText != "V1")
                    {
                        //Begin exiting if wrong version
                        sendWrongVersion(uploaderID, fileNameHelper(fileName));
                        errorCode = (int)ErrorCodes.wrong_version;
                        log.Error(string.Format("Inserting error code {0}:{1} into upload table", ErrorCodes.wrong_version, (int)ErrorCodes.wrong_version));
                        return null;
                    }
                }
                else
                {
                    //Begin exiting if no version on form
                    sendWrongVersion(uploaderID, fileNameHelper(fileName));
                    errorCode = (int)ErrorCodes.wrong_version;
                    log.Error(string.Format("Inserting error code {0}:{1} into upload table", ErrorCodes.wrong_version, (int)ErrorCodes.wrong_version));
                    return null;
                }

                try
                {
                    //Gets all data on the form via tags
                    log.Info(string.Format("Parsing XML."));
                    //docpart.document.firstchild is the entire xml document
                    //if we get the child elements we get a list of first children which should be 9
                    //we select the 3rd child which is the main table that gets filled out
                    var docTable = docPart.Document.FirstChild.ChildElements[2];
                    //the first 2 children of this table are grid settings and properties which we dont care about right now
                    docTable.RemoveAllChildren<TableGrid>();
                    docTable.RemoveAllChildren<TableProperties>();
                    //now we have 29 children of type w:tr which are the rows of the table
                    //select all the table cells inside the current table that arent a section header. 
                    //currently headers start with a number so excluded those
                    var tableCells = docTable.Descendants<TableCell>().Except(docTable.Descendants<TableCell>().Where( x => "0123456789".Contains(x.InnerText.Trim().Substring(0,1))));

                    //Grab the version cell and add it to ciwInformation
                    var versionNode = xml.SelectSingleNode(string.Format("w:body/w:tbl/w:tr/w:tc/w:tbl/w:tr/w:tc"), nameSpaceManager).NextSibling;
                    ciwInformation.Add(new CIWData { InnerText = versionNode.InnerText, TagName = versionNode.ChildNodes[1].ChildNodes[0].ChildNodes[1].Attributes[0].Value });

                    //get all table cells and add them after the version in ciwInformation
                    ciwInformation.AddRange( tableCells
                                        .Select
                                            (
                                                s =>
                                                    new CIWData
                                                    {
                                                        TagName = s.ChildElements.OfType<SdtBlock>().FirstOrDefault().GetFirstChild<SdtProperties>().GetFirstChild<Tag>().Val,
                                                        InnerText =ParseXML( s.ChildElements.OfType<SdtBlock>().FirstOrDefault().InnerText, s.OuterXml),
                                                    }
                                            ).ToList());
                }
                catch (Exception e)
                {
                    log.Error(string.Format("XML Parsing Failed - {0} with inner exception: {1}", e.Message, e.InnerException));
                    sendWrongVersion(uploaderID, fileNameHelper(fileName));


                    errorCode = (int)ErrorCodes.wrong_version;
                    log.Error(string.Format("Inserting error code {0}:{1} into upload table", ErrorCodes.wrong_version, (int)ErrorCodes.wrong_version));
                    return null;
                }


                //used in log
                string lastFirst = (ciwInformation.FirstOrDefault(c => c.TagName == "Employee-LastName").InnerText ?? "null") + ", " + (ciwInformation.FirstOrDefault(c => c.TagName == "Employee-FirstName").InnerText ?? "null");

                log.Info(String.Format("CiwInformation obtained for {0}", lastFirst));

                log.Info(String.Format("Creating temp file for {0}", lastFirst));

                //Create a temp csv file of the information within the form
                string tempFile = CreateTempFile(ciwInformation);
                errorCode = (int)ErrorCodes.successfully_processed;

                return tempFile;
            }
        }

        /// <summary>
        /// Function that is called if wrong version detected.
        /// Calls sendEmail constructor and function to send email for wrong version.
        /// </summary>
        /// <param name="uploaderID"></param>
        /// <param name="fileName"></param>
        private void sendWrongVersion(int uploaderID, string fileName)
        {
            CIWEMails sendEmails = new CIWEMails(uploaderID, "", "", "", "", fileName);

            sendEmails.SendWrongVersion();
        }

        /// <summary>
        /// Function that is called if password protection detected
        /// Calls sendEmail constructor and function to send email for password protection.
        /// </summary>
        /// <param name="uploaderID"></param>
        /// <param name="fileName"></param>
        private void sendPasswordProtection(int uploaderID, string fileName)
        {
            CIWEMails sendEmails = new CIWEMails(uploaderID, "", "", "", "", fileName);

            sendEmails.SendPasswordProtection();
        }

        /// <summary>
        /// Removes end of filename
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        private string fileNameHelper(string fileName)
        {

            int _ = fileName.LastIndexOf("_");
            if (_ < 0)
                return fileName;
            else
            {
                string _name = fileName.Remove(_, fileName.Length - _ - 5);
                return _name;
            }                
        }
                
        /// <summary>
        /// Retrieves the node
        /// </summary>
        /// <param name="innerText"></param>
        /// <param name="outerXML"></param>
        /// <returns>The text object in a field or the selected list item value</returns>
        private string ParseXML(string innerText, string outerXML)
        {
            //if xml contains dropdown list then parse and return value otherwise return inner xml
            XmlDocument xml = new XmlDocument();

            if (!String.IsNullOrEmpty(outerXML))
            {
                xml.InnerXml = outerXML;
            }

            // Add the namespace.
            XmlNamespaceManager nameSpaceManager = new XmlNamespaceManager(xml.NameTable);

            nameSpaceManager.AddNamespace("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");

            //check if it is a list
            XmlNodeList elemList = xml.GetElementsByTagName("w:listItem");

            if (elemList.Count == 0)
                return innerText;

            //Properly retrieve selected value of dropdown items
            else
            {
                if (!String.IsNullOrEmpty(innerText))
                {
                    XmlNode a = xml.SelectSingleNode(string.Format("w:tc/w:sdt/w:sdtPr/w:dropDownList/w:listItem[@w:displayText=\"{0}\"]", innerText), nameSpaceManager);

                    if (a.Attributes.Count > 1)
                    {
                        if (a.Attributes[1].Value != null)
                        {
                            return a.Attributes[1].Value;
                        }
                    }
                    else return a.Attributes[0].Value;
                }
            }

            return innerText;
        }

        /// <summary>
        /// Helper function to check for child care applicant on CIW
        /// </summary>
        /// <param name="ciwInformation"></param>
        /// <returns></returns>
        private bool CheckIfChildCare(List<CIW> ciwInformation)
        {
            return ciwInformation.First().ContractorType == "Child Care" || ciwInformation.First().InvestigationTypeRequested == "Tier 1C";
        }

        /// <summary>
        /// Processes data after CIW converted to CSV
        /// </summary>
        /// <param name="uploaderID"></param>
        /// <param name="filePath"></param>
        /// <param name="isDebug"></param>
        /// <returns>Int success code</returns>
        public int ProcessCIWInformation(int uploaderID, string filePath, bool isDebug)
        {
            log.Info("Processing CIW");

            //Create validation object
            ValidateCIW validate = new Validation.ValidateCIW();

            List<CIW> ciwInformation = new List<CIW>();

            log.Info(string.Format("Getting file data from temp csv file."));

            //Gets list of CIW's after mapping from csv files
            ciwInformation = GetFileData<CIW, CIWMapping>(filePath, config);

            CIWEMails sendEmails = new CIWEMails(uploaderID, ciwInformation.First().FirstName, ciwInformation.First().MiddleName,
                                                 ciwInformation.First().LastName, ciwInformation.First().Suffix, Path.GetFileName(filePath),
                                                 CheckIfChildCare(ciwInformation));

            //Delete temp csv file before proceeding
            try
            {
                log.Info(string.Format("Deleting Temp CSV File {0}.", filePath));
                File.Delete(filePath);
            }
            catch (IOException e)
            {
                log.Error("Unable to delete temp file" + e.Message);
                return 0;
            }

            log.Info("Processing " + ciwInformation.First().FullNameForLog);

            log.Info(string.Format("Checking version number. Current version is {0}", ciwInformation.First().VersionNumber));

            //Check version and begin exit if wrong version
            if (ciwInformation.First().VersionNumber != ConfigurationManager.AppSettings["VERSION"])
            {
                log.Error("Sending Wrong Version Number E-Mail");
                sendEmails.SendWrongVersion();
                log.Error(string.Format("Inserting error code {0}:{1} into upload table", ErrorCodes.wrong_version, (int)ErrorCodes.wrong_version));
                return (int)ErrorCodes.wrong_version;

            }
            else
                log.Info(string.Format("Version OK"));

            log.Info(string.Format("Checking if ARRA. ARRA selected is: {0}", ciwInformation.First().ArraLongTermContractor));

            //Check if ARRA contractor and begin exit if ARRA
            if (ciwInformation.First().ArraLongTermContractor == "Yes")
            {
                log.Error("Sending ARRA E-Mail");
                sendEmails.SendARRA();
                log.Error(string.Format("Inserting error code {0}:{1} into upload table", ErrorCodes.arra, (int)ErrorCodes.arra));
                return (int)ErrorCodes.arra;
            }
            else
                log.Info(string.Format("ARRA is OK"));

            log.Info(String.Format("Checking if {0} is a duplicate user", ciwInformation.First().FullNameForLog));

            //Check if duplicate and begin exit if duplicate exists
            if (!validate.IsDuplicate(ciwInformation))
            {
                log.Error(String.Format("Duplicate user found for {0}", ciwInformation.First().FullNameForLog));
                sendEmails.SendDuplicateUser();
                log.Error(string.Format("Inserting error code {0}:{1} into upload table", ErrorCodes.duplicate_user, (int)ErrorCodes.duplicate_user));
                return (int)ErrorCodes.duplicate_user;
            }

            log.Info(String.Format("No existing user found for {0}", ciwInformation.First().FullNameForLog));

            log.Info(String.Format("Company Name Primary is : {0}", !string.IsNullOrWhiteSpace(ciwInformation.FirstOrDefault().CompanyName) ? ciwInformation.FirstOrDefault().CompanyName : "No Company Name Primary"));
            log.Info(String.Format("Company Name Sub is : {0}", !string.IsNullOrWhiteSpace(ciwInformation.FirstOrDefault().CompanyNameSub) ? ciwInformation.FirstOrDefault().CompanyNameSub : "No Company Name Sub"));
            log.Info(String.Format("Checking if form is valid for user {0}", ciwInformation.First().FullNameForLog));

            

            //Validation is called inside if statement
            if (validate.IsFormValid(ciwInformation))
            {
                log.Info(String.Format("Form is valid for user {0}", ciwInformation.First().FullNameForLog));

                //Create object to begin insertion of ciw into database
                InsertCIW sd = new InsertCIW(ciwInformation.First(), uploaderID);

                int persID = 0;

                //Save the data
                log.Info(String.Format("Begin inserting CIW for {0}", ciwInformation.First().FullNameForLog));
                persID = sd.SaveCIW();

                //Begin sponsorship if successful
                if (persID > 0)
                    sendEmails.SendSponsorshipEMail(persID);
                log.Error(string.Format("Inserting error code {0}:{1} into upload table", ErrorCodes.successfully_processed, (int)ErrorCodes.successfully_processed));
                return (int)ErrorCodes.successfully_processed;
            }
            else
            {
                log.Error(String.Format("Form failed validation for user {0}", ciwInformation.First().FullNameForLog));

                //E-Mail Failure Template
                //Send error email
                Tuple<ValidationResult, ValidationResult, ValidationResult,
                        ValidationResult, ValidationResult, ValidationResult> ValidationErrors = new Tuple<ValidationResult, ValidationResult, ValidationResult,
                                                                                                            ValidationResult, ValidationResult, ValidationResult>(null, null,
                                                                                                                                                                null, null, null, null);

                log.Info(string.Format("Getting errors"));

                ValidationErrors = validate.GetErrors();

                log.Info(string.Format("{0} errors returned", CountErrors(ValidationErrors)));

                //send error email which contains a list of each sections errors
                sendEmails.SendErrors(ValidationErrors.Item1, ValidationErrors.Item2, ValidationErrors.Item3,
                                       ValidationErrors.Item4, ValidationErrors.Item5, ValidationErrors.Item6);
                log.Error(string.Format("Inserting error code {0}:{1} into upload table", ErrorCodes.failed_validation, (int)ErrorCodes.failed_validation));
                return (int)ErrorCodes.failed_validation;
            }
        }

        /// <summary>
        /// Counts the total number of errors
        /// </summary>
        /// <param name="t"></param>
        /// <returns>Count of errors</returns>
        private int CountErrors(Tuple<ValidationResult, ValidationResult, ValidationResult, ValidationResult, ValidationResult, ValidationResult> t)
        {
            var count = t.Item1.Errors.Count + t.Item2.Errors.Count + t.Item3.Errors.Count + t.Item4.Errors.Count + t.Item5.Errors.Count + t.Item6.Errors.Count;
            return count;
        }

        /// <summary>
        /// Generates temp CSV file separated by ||
        /// </summary>
        /// <param name="ciwData"></param>
        private string CreateTempFile(List<CIWData> ciwData)
        {
             //Get first and last name
            string first = ciwData.First(c => c.TagName == "Employee-FirstName").InnerText;
            string last = ciwData.First(c => c.TagName == "Employee-LastName").InnerText;

            //If either is null or empty then use placeholder name
            first = (first == null ? "FirstNameNull" : (first == "" ? "FirstNameEmpty" : first));
            last = (last == null ? "LastNameNull" : (last == "" ? "LastNameEmpty" : last));

            //uses first 20 characters of first and last name and adds time stamp to end and then .csv
            string csvFileName = first.Length >= 20 ? first.Substring(0, 20) : first.Substring(0, first.Length) + "_" + (last.Length >= 20 ? last.Substring(0, 20) : last.Substring(0, last.Length)) + "_" + DateTime.Now.ToString("MMddyyyy_HHmmss") + ".csv";

            log.Info("CIW Info Count: " + ciwData.Count);

            string fileName = ConfigurationManager.AppSettings["TEMPFOLDER"] + csvFileName;

            try
            {
                using (StreamWriter writer = new StreamWriter(fileName, false))
                {
                    writer.WriteLine(string.Join("||", ciwData.Select(item => item.TagName)));
                    writer.WriteLine(string.Join("||", ciwData.Select(item => item.InnerText)));
                }
            }
            catch (Exception ex)
            {
                log.Error(ex.Message + " - " + ex.InnerException);
            }

            log.Info(string.Format("Temp csv file {0} created", fileName));

            return fileName;
        }

        /// <summary>
        /// Loads the CIW information
        /// </summary>
        /// <typeparam name="TClass"></typeparam>
        /// <typeparam name="TMap"></typeparam>
        /// <param name="filePath"></param>
        /// <param name="config"></param>
        /// <returns>List of CIW's</returns>
        private List<TClass> GetFileData<TClass, TMap>(string filePath, CsvConfiguration config)
            where TClass : class
            where TMap : CsvClassMap<TClass>
        {
            log.Info(string.Format("Parsing CSV file {0} and mapping to CIW object", filePath));

            using (CsvParser csvParser = new CsvParser(new StreamReader(filePath), config))
            {
                using (CsvReader csvReader = new CsvReader(csvParser))
                {
                    csvReader.Configuration.RegisterClassMap<TMap>();

                    return csvReader.GetRecords<TClass>().ToList();
                }
            }
        }
    }
}