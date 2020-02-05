using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Tor;
using Tor.Config;

namespace PolarityTor
{
    public partial class PolarityTorClient : Form
    {
        private Client client;
        private volatile bool closing;
        private ORConnectionCollection connections;
        private CircuitCollection circuits;
        private StreamCollection streams;
        private RouterCollection allRouters;

        public PolarityTorClient()
        {
            InitializeComponent();
        }

        private void PolarityTorClient_Load(object sender, EventArgs e)
        {
            initializeTor();
        }

        private void initializeTor()
        {
            Process[] previous = Process.GetProcessesByName("tor");

            SetStatusProgress(Constants.PROGRESS_INDETERMINATE);

            if (previous != null && previous.Length > 0)
            {
                SetStatusText("Killing previous tor instances..");

                foreach (Process process in previous)
                    process.Kill();
            }

            SetStatusText("Creating the tor client..");

            ClientCreateParams createParameters = new ClientCreateParams();
            createParameters.ConfigurationFile = ConfigurationManager.AppSettings["torConfigurationFile"]; // Gets values from app.config
            createParameters.ControlPassword = ConfigurationManager.AppSettings["torControlPassword"];
            createParameters.ControlPort = Convert.ToInt32(ConfigurationManager.AppSettings["torControlPort"]);
            createParameters.DefaultConfigurationFile = ConfigurationManager.AppSettings["torDefaultConfigurationFile"];
            createParameters.Path = ConfigurationManager.AppSettings["torPath"];

            createParameters.SetConfig(ConfigurationNames.AvoidDiskWrites, true);
            createParameters.SetConfig(ConfigurationNames.GeoIPFile, Path.Combine(Environment.CurrentDirectory, @"Tor\Data\geoip"));
            createParameters.SetConfig(ConfigurationNames.GeoIPv6File, Path.Combine(Environment.CurrentDirectory, @"Tor\Data\geoip6"));

            client = Client.Create(createParameters);

            if (!client.IsRunning)
            {
                SetStatusProgress(Constants.PROGRESS_DISABLED);
                SetStatusText("The tor client could not be created");
                return;
            }

            client.Status.BandwidthChanged += OnClientBandwidthChanged;
            client.Status.CircuitsChanged += OnClientCircuitsChanged;
            client.Status.ORConnectionsChanged += OnClientConnectionsChanged;
            client.Status.StreamsChanged += OnClientStreamsChanged;
            client.Configuration.PropertyChanged += (s, e) => { Invoke((Action)delegate { configGrid.Refresh(); }); };
            client.Shutdown += new EventHandler(OnClientShutdown);

            SetStatusProgress(Constants.PROGRESS_DISABLED);
            SetStatusText("Ready");

            configGrid.SelectedObject = client.Configuration;

            SetStatusText("Downloading routers");
            SetStatusProgress(Constants.PROGRESS_INDETERMINATE);

            try
            {
                ThreadPool.QueueUserWorkItem(state =>
                {
                    allRouters = client.Status.GetAllRouters();

                    if (allRouters == null)
                    {
                        SetStatusText("Could not download routers");
                        SetStatusProgress(Constants.PROGRESS_DISABLED);
                    }
                    else
                    {
                        Invoke((Action)delegate
                        {

                            foreach (Router router in allRouters)
                            {
                                routerGridView.Rows.Add(router.Nickname, router.IPAddress, string.Format ("{0}/s", router.Bandwidth));
                            }
                        });

                        SetStatusText("Ready");
                        SetStatusProgress(Constants.PROGRESS_DISABLED);
                        ShowTorReady();
                    }
                });
            }
            catch (Exception e)
            {
                MessageBox.Show(e.ToString());
            }
        }

        private void ShowTorReady()
        {
            InvokeOnUiThreadIfRequired(() =>
            {
                lblConnection.Visible = true;
                this.BackColor = Color.PaleGreen ;
            });
        }

        private void SetStatusProgress(string txt)
        {
            InvokeOnUiThreadIfRequired(() => lblStatus.Text = txt);
        }

        private void SetStatusText(string txt)
        {
            InvokeOnUiThreadIfRequired(() => lblStatusText.Text = txt);
        }

        public void InvokeOnUiThreadIfRequired(Action action) // Prevents any cross threading errors.
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(action);
            }
            else
            {
                action.Invoke();
            }
        }

        private void OnClientShutdown(object sender, EventArgs e)
        {
            if (!closing)
            {
                MessageBox.Show("The tor client has been terminated without warning", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            client = null;

            Invoke((Action)delegate { Close(); });
        }

        private void OnClientBandwidthChanged(object sender, BandwidthEventArgs e)
        {
            if (closing)
                return;

            Invoke((Action)delegate
            {
                if (e.Downloaded.Value == 0 && e.Uploaded.Value == 0)
                {
                    lblUp.Text = "0 KBytes/s";
                    lblDown.Text = "0 KBytes/s";
            }  else{
                    lblUp.Text = e.Uploaded + "/s";
                    lblDown.Text = e.Downloaded + "/s";
                }
            });
        }

        private void OnClientCircuitsChanged(object sender, EventArgs e)
        {
            if (closing)
                return;

            circuits = client.Status.Circuits;

            Invoke((Action)delegate
            {
                circuitTree.BeginUpdate();

                List<TreeNode> removals = new List<TreeNode>();

                foreach (TreeNode n in circuitTree.Nodes)
                    removals.Add(n);

                foreach (Circuit circuit in circuits)
                {
                    bool added = false;
                    TreeNode node = null;

                    if (!showClosedCheckBox.Checked)
                        if (circuit.Status == CircuitStatus.Closed || circuit.Status == CircuitStatus.Failed)
                            continue;

                    foreach (TreeNode existingNode in circuitTree.Nodes)
                        if (((Circuit)existingNode.Tag).ID == circuit.ID)
                        {
                            node = existingNode;
                            break;
                        }

                    string text = string.Format("Circuit #{0} [{1}] ({2})", circuit.ID, circuit.Status, circuit.Routers.Count);
                    string tooltip = string.Format("Created: {0}\nBuild Flags: {1}", circuit.TimeCreated, circuit.BuildFlags);

                    if (node == null)
                    {
                        node = new TreeNode(text);
                        //AUZ node.ContextMenuStrip = circuitMenuStrip;
                        node.Tag = circuit;
                        node.ToolTipText = tooltip;
                        added = true;
                    }
                    else
                    {
                        node.Text = text;
                        node.ToolTipText = tooltip;
                        node.Nodes.Clear();

                        removals.Remove(node);
                    }

                    foreach (Router router in circuit.Routers)
                        node.Nodes.Add(string.Format("{0} [{1}] ({2}/s)", router.Nickname, router.IPAddress, router.Bandwidth));

                    if (added)
                        circuitTree.Nodes.Add(node);
                }

                foreach (TreeNode remove in removals)
                    circuitTree.Nodes.Remove(remove);

                circuitTree.EndUpdate();
            });
        }

        private void OnClientConnectionsChanged(object sender, EventArgs e)
        {
            if (closing)
                return;

            connections = client.Status.ORConnections;

            Invoke((Action)delegate
            {
                connectionTree.BeginUpdate();

                List<TreeNode> removals = new List<TreeNode>();

                foreach (TreeNode n in connectionTree.Nodes)
                    removals.Add(n);

                foreach (ORConnection connection in connections)
                {
                    bool added = false;
                    TreeNode node = null;

                    if (!showClosedCheckBox.Checked)
                        if (connection.Status == ORStatus.Closed || connection.Status == ORStatus.Failed)
                            continue;

                    foreach (TreeNode existingNode in connectionTree.Nodes)
                    {
                        ORConnection existing = (ORConnection)existingNode.Tag;

                        if (connection.ID != 0 && connection.ID == existing.ID)
                        {
                            node = existingNode;
                            break;
                        }
                        if (connection.Target.Equals(existing.Target, StringComparison.CurrentCultureIgnoreCase))
                        {
                            node = existingNode;
                            break;
                        }
                    }

                    string text = string.Format("Connection #{0} [{1}] ({2})", connection.ID, connection.Status, connection.Target);

                    if (node == null)
                    {
                        node = new TreeNode(text);
                        node.Tag = connection;
                        added = true;
                    }
                    else
                    {
                        node.Text = text;
                        node.Nodes.Clear();

                        removals.Remove(node);
                    }

                    if (added)
                        connectionTree.Nodes.Add(node);
                }

                foreach (TreeNode remove in removals)
                    connectionTree.Nodes.Remove(remove);

                connectionTree.EndUpdate();
            });
        }

        private void OnClientStreamsChanged(object sender, EventArgs e)
        {
            if (closing)
                return;

            streams = client.Status.Streams;

            Invoke((Action)delegate
            {
                streamsTree.BeginUpdate();

                List<TreeNode> removals = new List<TreeNode>();

                foreach (TreeNode n in streamsTree.Nodes)
                    removals.Add(n);

                foreach (Tor.Stream stream in streams)
                {
                    bool added = false;
                    TreeNode node = null;

                    if (!showClosedCheckBox.Checked)
                        if (stream.Status == StreamStatus.Failed || stream.Status == StreamStatus.Closed)
                            continue;

                    foreach (TreeNode existingNode in streamsTree.Nodes)
                        if (((Tor.Stream)existingNode.Tag).ID == stream.ID)
                        {
                            node = existingNode;
                            break;
                        }

                    Circuit circuit = null;

                    if (stream.CircuitID > 0)
                        circuit = circuits.Where(c => c.ID == stream.CircuitID).FirstOrDefault();

                    string text = string.Format("Stream #{0} [{1}] ({2}, {3})", stream.ID, stream.Status, stream.Target, circuit == null ? "detached" : "circuit #" + circuit.ID);
                    string tooltip = string.Format("Purpose: {0}", stream.Purpose);

                    if (node == null)
                    {
                        node = new TreeNode(text);
                        //AUZ node.ContextMenuStrip = streamMenuStrip;
                        node.Tag = stream;
                        node.ToolTipText = tooltip;
                        added = true;
                    }
                    else
                    {
                        node.Text = text;
                        node.ToolTipText = tooltip;
                        node.Nodes.Clear();

                        removals.Remove(node);
                    }

                    if (added)
                        streamsTree.Nodes.Add(node);
                }

                foreach (TreeNode remove in removals)
                    streamsTree.Nodes.Remove(remove);

                streamsTree.EndUpdate();
            });
        }

        private void PolarityTor_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (client != null && client.IsRunning)
            {
                closing = true;
                client.Status.BandwidthChanged -= OnClientBandwidthChanged;
                client.Status.CircuitsChanged -= OnClientCircuitsChanged;
                client.Dispose();

                e.Cancel = true;
            }

        }

        private void newCircuitButton_Click(object sender, EventArgs e)
        {
            if (client.IsRunning)
                client.Controller.CreateCircuit();
        }

        private void showClosedCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            OnClientCircuitsChanged(client, EventArgs.Empty);
            OnClientConnectionsChanged(client, EventArgs.Empty);
            OnClientStreamsChanged(client, EventArgs.Empty);
        }
    }
}
