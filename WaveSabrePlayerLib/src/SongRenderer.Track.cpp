#include <WaveSabrePlayerLib/SongRenderer.h>

using namespace WaveSabreCore;

namespace WaveSabrePlayerLib
{
	SongRenderer::Track::Track(SongRenderer *songRenderer, SongRenderer::DeviceFactory factory)
	{
		for (int i = 0; i < numBuffers; i++) Buffers[i] = new float[songRenderer->sampleRate];

		this->songRenderer = songRenderer;

		volume = songRenderer->readFloat();

		NumReceives = songRenderer->readInt();
		if (NumReceives)
		{
			Receives = new Receive[NumReceives];
			sendAutomations = new MixerAutomation *[NumReceives];
			for (int i = 0; i < NumReceives; i++)
			{
				Receives[i].SendingTrackIndex = songRenderer->readInt();
				Receives[i].ReceivingChannelIndex = songRenderer->readInt();
				Receives[i].Volume = songRenderer->readFloat();
				sendAutomations[i] = nullptr;
			}
		}

		numDevices = songRenderer->readInt();
		if (numDevices)
		{
			devicesIndicies = new int[numDevices];
			for (int i = 0; i < numDevices; i++)
			{
				devicesIndicies[i] = songRenderer->readInt();
			}
		}

		midiLaneId = songRenderer->readInt();

		numAutomations = songRenderer->readInt();
		if (numAutomations)
		{
			automations = new Automation *[numAutomations];
			for (int i = 0; i < numAutomations; i++)
			{
				int deviceIndex = songRenderer->readInt();
				automations[i] = new Automation(songRenderer, songRenderer->devices[devicesIndicies[deviceIndex]]);
			}
		}

		volumeAutomation = nullptr;
		panAutomation = nullptr;
		numMixerAutomations = songRenderer->readInt();
		if (numMixerAutomations)
		{
			mixerAutomations = new MixerAutomation *[numMixerAutomations];
			for (int i = 0; i < numMixerAutomations; i++)
			{
				mixerAutomations[i] = new MixerAutomation(songRenderer);
				switch (mixerAutomations[i]->target)
				{
				case MixerAutomation::Target::Volume:
					volumeAutomation = mixerAutomations[i];
					break;
				case MixerAutomation::Target::Pan:
					panAutomation = mixerAutomations[i];
					break;
				case MixerAutomation::Target::SendVolume:
					if (mixerAutomations[i]->sendIndex >= 0 && mixerAutomations[i]->sendIndex < NumReceives)
						sendAutomations[mixerAutomations[i]->sendIndex] = mixerAutomations[i];
					break;
				}
			}
		}

		lastSamplePos = 0;
		accumEventTimestamp = 0;
		eventIndex = 0;
	}

	SongRenderer::Track::~Track()
	{
		for (int i = 0; i < numBuffers; i++) delete [] Buffers[i];

		if (NumReceives)
		{
			delete [] Receives;
			delete [] sendAutomations;
		}
		
		if (numDevices)
		{
			delete[] devicesIndicies;
		}

		if (numAutomations)
		{
			for (int i = 0; i < numAutomations; i++) delete automations[i];
			delete [] automations;
		}

		if (numMixerAutomations)
		{
			for (int i = 0; i < numMixerAutomations; i++) delete mixerAutomations[i];
			delete [] mixerAutomations;
		}
	}

	void SongRenderer::Track::Run(int numSamples)
	{
		MidiLane* lane = songRenderer->midiLanes[midiLaneId];
		for ( ; eventIndex < lane->numEvents; eventIndex++)
		{
			Event *e = &lane->events[eventIndex];
			int samplesToEvent = accumEventTimestamp + e->TimeStamp - lastSamplePos;
			if (samplesToEvent >= numSamples) break;
			switch (e->Type)
			{
			case EventType::NoteOn:
				for (int i = 0; i < numDevices; i++) songRenderer->devices[devicesIndicies[i]]->NoteOn(e->Note, e->Velocity, samplesToEvent);
				break;

			case EventType::NoteOff:
				for (int i = 0; i < numDevices; i++) songRenderer->devices[devicesIndicies[i]]->NoteOff(e->Note, samplesToEvent);
				break;
			}
			accumEventTimestamp += e->TimeStamp;
		}

		for (int i = 0; i < numAutomations; i++) automations[i]->Run(numSamples);

		for (int i = 0; i < numBuffers; i++) memset(Buffers[i], 0, numSamples * sizeof(float));
		for (int i = 0; i < NumReceives; i++)
		{
			Receive *r = &Receives[i];
			float **receiveBuffers = songRenderer->tracks[r->SendingTrackIndex]->Buffers;
			float vol = sendAutomations[i] ? sendAutomations[i]->Evaluate(lastSamplePos) : r->Volume;
			for (int j = 0; j < 2; j++)
			{
				for (int k = 0; k < numSamples; k++) Buffers[j + r->ReceivingChannelIndex][k] += receiveBuffers[j][k] * vol;
			}
		}

		for (int i = 0; i < numDevices; i++) songRenderer->devices[devicesIndicies[i]]->Run((double)lastSamplePos / Helpers::CurrentSampleRate, Buffers, Buffers, numSamples);

		if (volumeAutomation)
		{
			for (int j = 0; j < numSamples; j++)
			{
				float v = volumeAutomation->Evaluate(lastSamplePos + j);
				for (int i = 0; i < numBuffers; i++) Buffers[i][j] *= v;
			}
		}
		else if (volume != 1.0f)
		{
			for (int i = 0; i < numBuffers; i++)
			{
				for (int j = 0; j < numSamples; j++) Buffers[i][j] *= volume;
			}
		}

		if (panAutomation)
		{
			float pan = panAutomation->Evaluate(lastSamplePos);
			float lGain = Helpers::PanToScalarLeft(pan);
			float rGain = Helpers::PanToScalarRight(pan);
			for (int i = 0; i < numBuffers; i += 2)
			{
				for (int j = 0; j < numSamples; j++) Buffers[i][j] *= lGain;
			}
			for (int i = 1; i < numBuffers; i += 2)
			{
				for (int j = 0; j < numSamples; j++) Buffers[i][j] *= rGain;
			}
		}

		lastSamplePos += numSamples;
	}

	SongRenderer::Track::Automation::Automation(SongRenderer *songRenderer, WaveSabreCore::Device *device)
	{
		this->device = device;
		paramId = songRenderer->readInt();
		numPoints = songRenderer->readInt();
		points = new Point[numPoints];
		int lastPointTime = 0;
		for (int i = 0; i < numPoints; i++)
		{
			int absTime = lastPointTime + songRenderer->readInt();
			points[i].TimeStamp = absTime;
			lastPointTime = absTime;
			points[i].Value = (float)((double)songRenderer->readByte() / 255.0);
		}
		samplePos = 0;
		pointIndex = 0;
	}

	SongRenderer::Track::Automation::~Automation()
	{
		delete [] points;
	}

	void SongRenderer::Track::Automation::Run(int numSamples)
	{
		for ( ; pointIndex < numPoints; pointIndex++)
		{
			if (points[pointIndex].TimeStamp > samplePos) break;
		}
		if (pointIndex >= numPoints)
		{
			device->SetParam(paramId, points[numPoints - 1].Value);
		}
		else if (pointIndex <= 0)
		{
			device->SetParam(paramId, points[0].Value);
		}
		else
		{
			int timestampDelta = points[pointIndex].TimeStamp - points[pointIndex - 1].TimeStamp;
			float mixAmount = timestampDelta > 0 ?
				(float)(samplePos - points[pointIndex - 1].TimeStamp) / (float)timestampDelta :
				0.0f;
			device->SetParam(paramId, Helpers::Mix(points[pointIndex - 1].Value, points[pointIndex].Value, mixAmount));
		}
		samplePos += numSamples;
	}

	SongRenderer::Track::MixerAutomation::MixerAutomation(SongRenderer *songRenderer)
	{
		target = (Target)songRenderer->readByte();
		sendIndex = songRenderer->readInt();
		numPoints = songRenderer->readInt();
		points = new Point[numPoints];
		int lastPointTime = 0;
		for (int i = 0; i < numPoints; i++)
		{
			int absTime = lastPointTime + songRenderer->readInt();
			points[i].TimeStamp = absTime;
			lastPointTime = absTime;
			points[i].Value = (float)((double)songRenderer->readByte() / 255.0);
		}
		pointIndex = 0;
	}

	SongRenderer::Track::MixerAutomation::~MixerAutomation()
	{
		delete [] points;
	}

	float SongRenderer::Track::MixerAutomation::Evaluate(int absoluteSample)
	{
		while (pointIndex < numPoints && points[pointIndex].TimeStamp <= absoluteSample)
			pointIndex++;
		if (pointIndex >= numPoints) return points[numPoints - 1].Value;
		if (pointIndex == 0) return points[0].Value;
		int timestampDelta = points[pointIndex].TimeStamp - points[pointIndex - 1].TimeStamp;
		float mixAmount = timestampDelta > 0
			? (float)(absoluteSample - points[pointIndex - 1].TimeStamp) / (float)timestampDelta
			: 0.0f;
		return Helpers::Mix(points[pointIndex - 1].Value, points[pointIndex].Value, mixAmount);
	}
}
